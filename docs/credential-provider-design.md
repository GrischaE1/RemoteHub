# RemoteHub Agent – Automatic sign-in (credential provider)

Status: Draft v1 – ready for implementation and the separate security review asked for in [agent-desktop-draft.md](agent-desktop-draft.md) §4.4.
Owner: Grischa Ernst

Covers the backlog item "automatic sign-in with stored credentials (credential provider)" from [agent-spec.md](agent-spec.md) §12 and decision 5 in [agent-desktop-draft.md](agent-desktop-draft.md) §2.

## 1. Goal

When the operator opens the agent desktop on a device where **nobody is signed in on the console**, RemoteHub can sign in with the connection's stored credentials. The operator then lands on the desktop without typing the password into the sign-in screen.

This is the most sensitive part of the agent. A credential provider runs inside `LogonUI.exe` as LocalSystem and decides who gets an interactive session on the device. A bug here can:
- let an attacker sign in;
- leak passwords;
- break the sign-in screen for everyone.

### 1.1 Security baseline

**Automatic sign-in must not allow anything that an operator with the `SecureDesktop` right couldn't already do by typing the same password into the sign-in screen through the agent desktop.**

Each rule below is there to keep that true. Every design choice is judged against this baseline. The feature adds convenience and must not add reach.

## 2. Non-goals (v1)

- **Unlocking a locked session.** Only `CPUS_LOGON` is supported, not `CPUS_UNLOCK_WORKSTATION`. Unlocking could come later in phase 2 (§12), and only for the session of the same account.
- **Signing in as a different account** from the one that authenticated the agent connection.
- **RDP sessions' sign-in screens and Remote Desktop Services servers.** Only the physical console session is supported.
- **Storing anything on the device:**
  - no Winlogon `AutoAdminLogon` / `DefaultPassword` (that's an LSA secret, readable by any admin, and persistent);
  - no LSA secret;
  - no file and no registry value.
- **Changing an expired password, smart card, Windows Hello and FIDO.** A sign-in that needs any of these fails, with a clear reason.
- **Credential UI prompts** (`CPUS_CREDUI`), changing the password (`CPUS_CHANGE_PASSWORD`) and a credential provider filter. The provider never hides or wraps other providers.

## 3. Threat model

| # | Attacker | Goal | Main defences |
|---|---|---|---|
| T1 | On the network, no credentials | Trigger a sign-in, or get passwords | Pinned TLS, Windows sign-in, new right `AutoSignIn` (default: nobody), policy off by default, `SignInGate` throttle (§5, §6) |
| T2 | Operator with a desktop right but not `AutoSignIn` | Sign in without the right | The right is checked in the service, not trusted from RemoteHub (§6.2) |
| T3 | Operator with `AutoSignIn` | Sign in as **another** account, or into an occupied session | Only the account that authenticated the connection (same SID); console must be in the `SignIn` state; never ends or takes over a session (§6.3) |
| T4 | Local user without admin rights on the device | Inject credentials into the provider, read them, or squat the pipe | Pipe owned by the service and created at start (`FIRST_PIPE_INSTANCE`), SYSTEM-only DACL, remote clients rejected, both ends check the peer process, one-shot ticket (§7) |
| T5 | Local user / malware without admin rights | Replace or hijack the provider DLL | Program Files ACLs, signed DLL, no DLL dependencies outside System32, fixed CLSID under HKLM (§8.4) |
| T6 | Person at the device | Use the admin session that was just signed in | Banner; the session is **locked or signed out** when the connection ends, including after a crash or a dropped connection (§9) |
| T7 | Memory disclosure (dump, crash report, pagefile) | Read the password | Password kept only in pinned buffers, protected with `CryptProtectMemory`, zeroed after use, never in a .NET `string`, never in logs (§10) |
| T8 | Compromised RemoteHub PC | — | Out of scope: it already has the stored passwords. Automatic sign-in adds nothing for it. |
| T9 | Local admin or SYSTEM on the device | — | Out of scope: they already control the device. |
| T10 | Bug or crash in the provider | Sign-in screen broken for everyone (denial of service) | Minimal native code, no blocking calls on LogonUI threads, input fuzzed, inert unless a ticket is pending, install is opt-in, policy kill switch (§8) |

## 4. Architecture

```
RemoteHub (operator PC)               Device
┌───────────────────────┐ pinned TLS  ┌──────────────────────────────────────────────┐
│ AgentDesktopSession    │ /v1/desktop │ RemoteHubAgent service (LocalSystem)          │
│  Hello.Rights.AutoSignIn│──────────▶ │  DesktopEndpoint                              │
│  SignInRequest (binary)│            │   ├ right + policy + session-state checks       │
│  ◀ SignInResult        │ ◀──────────│   ├ SID check: same account as the Basic sign-in│
└───────────────────────┘            │   └ AutoSignInBroker (one ticket per device)    │
                                     │        │ \\.\pipe\RemoteHubAgent.AutoSignIn      │
                                     │        │ SYSTEM-only, local only, peer-verified  │
                                     │        ▼                                         │
                                     │  LogonUI.exe (session 1, SYSTEM)                │
                                     │   └ RemoteHubCredentialProvider.dll (C++)       │
                                     │       KERB_INTERACTIVE_UNLOCK_LOGON → Winlogon/LSA │
                                     │                                                  │
                                     │  RemoteHubAgent.exe desktop (helper) – never     │
                                     │  sees the credentials                            │
                                     └──────────────────────────────────────────────┘
```

The credentials go along exactly one path: RemoteHub → service memory → pipe → provider → LSA.

They never touch:
- the desktop helper, which processes untrusted remote input;
- disk or the registry;
- logs;
- the WebSocket JSON serializer.

## 5. Enabling: three independent switches, all off by default

Automatic sign-in works only if **all** of these are on.

| Layer | Switch | Default | Why |
|---|---|---|---|
| Install | MSI property `AUTOSIGNIN=1` (also in the `.rhagent` file / install dialog). Without it, the provider DLL **isn't installed or registered** at all. | off | A device that never uses it has no extra code in LogonUI. |
| Device policy | `HKLM\SOFTWARE\Policies\RemoteHub\Agent` `NoUserBehavior` = `AutoSignIn` (REG_SZ). Missing / `SignInScreen` / unreadable → off. | off | IT decides per device (ADMX later). Read for every request. |
| Right | `Rights\AutoSignIn` (REG_MULTI_SZ, group SIDs). | **nobody** (like `ConsentBypass`) | Separate from `SecureDesktop`, so seeing the sign-in screen doesn't imply signing in automatically. |
| Client | Per connection: *Sign in automatically when nobody is signed in* (inherited like the other agent settings, default off). App policy `DisableAgentAutoSignIn` (DWORD 1) in `HKLM/HKCU\SOFTWARE\Policies\RemoteHub` hides it. | off | The operator decides per connection. RemoteHub never sends the password for this unasked. |

- **Kill switch:** Windows' own policy *Exclude credential providers* (`ExcludedCredentialProviders`) with our CLSID disables the provider on the device without uninstalling.
- **Policy cannot be read:** fail closed. `AgentPolicy.Refused` already sets everything off; `AutoSignIn` must be included there.

## 6. Protocol and service-side checks

### 6.1 Protocol changes (`Desktop/DesktopProtocol.cs`, protocol version stays 1, feature-flagged)

- `DesktopRights` gets `bool AutoSignIn`. It's true only if the policy is on, the right is granted, the provider is registered and the pipe is up. RemoteHub offers the option only when it's true.
- New feature string `DesktopFeatures.AutoSignIn = "autoSignIn"` in Hello.
- `DesktopStart` gets `bool AutoSignIn`. It's the operator's intent, and it's valid only when the selected session is the console in state `SignIn`.
- New message `SignInRequest` (RemoteHub → agent):
  - binary, **not JSON**: `[type][u16 len][UTF-16LE password bytes]`;
  - no user name, because the agent uses the account that authenticated (§6.3);
  - sent only after the agent asks for it with status `SignInReady`. RemoteHub never sends it on its own.
- New status states:
  - `SignInReady`: the agent is ready; RemoteHub now sends `SignInRequest`.
  - `SigningIn`.
  - `SignInFailed`: carries a reason code (wrong password, account locked, password expired, logon type not granted, restricted hours, smart card required, timeout, …). The message is generic and never echoes input.
- `AgentPolicy.Refused` and the defaults include `AutoSignIn` = nobody.

> Why the password is sent a second time instead of keeping it from the Basic header: today `AgentHost.SignIn` drops the password right after `LogonUser`, which is good. Keeping it for every connection "just in case" would put every operator's password in memory for the whole session. Sending it again only on explicit request keeps the exposure to a few seconds, and only for connections that use the feature.

### 6.2 Checks in `DesktopEndpoint` (all in the service; RemoteHub's word is never trusted)

The service checks these **in order**. Any failure → `SignInFailed`, an audit entry, and the connection continues on the plain sign-in screen.

1. `policy.NoUserBehavior == AutoSignIn` and `identity.InAny(policy.Groups(AgentRight.AutoSignIn))`, read again at request time.
2. The provider is registered (HKLM key present, CLSID matches) and the broker pipe was created successfully at service start.
3. Session checks:
   - the selected session is the **current console session** (`WTSGetActiveConsoleSessionId`);
   - its state is **`SignIn`**: no user, `LogonUI` running in it;
   - it's not `Locked` or `Disconnected`, and no user is signed in on the console.
4. The authenticated account has **no other session on the device** (`WTSEnumerateSessions`, compared by SID). On client Windows, signing in on the console would take over an existing RDP session of that account and disconnect the person using it. → Refused with "This account already has a session on the device – connect to that session instead".
5. Only one pending ticket per device (§7.3). A second request while one is pending → refused.
6. At most one attempt per desktop connection. At most 3 attempts per account per 15 minutes, counted in a new `SignInGate` bucket keyed by SID. This protects against account lockout when the stored password is out of date.
7. The `SignInRequest` arrives within 30 s of `SignInReady`. Otherwise the request is cancelled.

### 6.3 Which account – exactly the authenticated one

- `WindowsAuthenticator.Authenticate` already holds the logon token. Keep the account **SID** in `ShellIdentity` as its own property (today it's only mixed into `GroupSids`, `Shell.cs`), along with the name as Windows resolved it: `DOMAIN\user`; for local accounts, `COMPUTERNAME\user`.
- The ticket carries that domain and user. The client can't choose it.
- After the provider reports success, the service reads the new console session's user SID (`WTSQueryUserToken` → `TokenUser`). If it doesn't match: **log off that session immediately** (`WTSLogoffSession`), audit error 3101, and end the connection. This is a defence against bugs and races; it should never happen.
- Restrictions are **not bypassed** and simply fail the sign-in: "Deny log on locally", "Allow log on locally", logon hours, workstation restrictions, account disabled/locked, `Protected Users`, smart card required, Windows Hello for Business-only accounts, and the legal notice (`LegalNoticeText`).
  - The legal notice is shown before the providers. The operator dismisses it through the remote desktop like a local user would. The agent never clicks it away.

### 6.4 Flow

```
RemoteHub                    Service                                  LogonUI + provider
Start{session=console, AutoSignIn}
                             checks 6.2 (1–5)
                             helper starts on secure desktop (as today, HoldUserDesktop)
            ◀── Status SignInReady
SignInRequest(pw) ─────────▶ checks 6.2 (6–7); ticket = {SID, domain, user,
                             pw (protected), session, expires +60 s, nonce}
                             SAS if Ctrl+Alt+Del is required (SecureAttention.Send)
                             notify provider over pipe ───────────────▶ CredentialsChanged()
                                                                       GetCredentialCount: 1, auto-logon
                                                       ◀── pipe: Claim(nonce, session)
                             verify peer, hand out the ticket once ──▶ GetSerialization
                             zero the service copy                       KERB_INTERACTIVE_UNLOCK_LOGON
                                                                       zero the buffers
                                                       ◀── pipe: Result(NTSTATUS)  (ReportResult)
                             verify the new session's SID
            ◀── Status SignedIn / SignInFailed(reason)
                             helper follows the console (existing code); _allowedBy = identity.Account
```

- **Consent after sign-in:** the new session belongs to the operator's own account. `IsSelf` already lets it continue without asking. `_allowedBy` is set to `identity.Account` so that `HoldUserDesktop` releases the picture for this user only.
  - If anyone else's desktop shows up (a race, fast user switching), the existing ask/PIN rule applies unchanged.

## 7. Local channel: service ⇄ provider

### 7.1 The pipe

- **Name:** `\\.\pipe\RemoteHubAgent.AutoSignIn`.
- **Created once, when the service starts and before it listens on the port:**
  - with `FILE_FLAG_FIRST_PIPE_INSTANCE`, so nobody can create it first (pipe squatting);
  - if creating it fails, the feature is off until the service restarts, and that is logged.
- **DACL:** `D:P(A;;GA;;;SY)`. SYSTEM only: no Administrators, no interactive users, no `Everyone`. `PIPE_REJECT_REMOTE_CLIENTS`. The owner is SYSTEM.
- **Mode:** message mode and a fixed maximum message size (4 KB). Anything larger, malformed or out of order closes the connection.

### 7.2 Peer checks (both ends)

**The service checks the client** (`GetNamedPipeClientProcessId`, `GetNamedPipeClientSessionId`):
- the image is `%SystemRoot%\System32\LogonUI.exe`, checked through `QueryFullProcessImageNameW` and compared case-insensitively against the real system path, not a string from the client;
- the process token is SYSTEM;
- the client session is **the ticket's session**.

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
- **Cancelled when:** it expires, the RemoteHub connection ends, the console session changes or leaves the `SignIn` state, the policy is turned off, or the service stops.
- **Messages:** `Pending(nonce, session)` → `Claim(nonce, session)` → `Ticket(domain, user, password)` → `Result(nonce, NTSTATUS, substatus)`. No other message types exist. The provider can't ask for anything else, and the service never sends a ticket without a `Claim`.

## 8. The credential provider (new native project)

### 8.1 Project

- **`src/RemoteHub.CredentialProvider`:**
  - C++20, `vcxproj`, x64 (plus ARM64 if the agent ever ships ARM64);
  - static CRT (`/MT`);
  - no dependencies beyond Windows system DLLs (`secur32`, `credui` only if `CredPackAuthenticationBuffer` is used, `advapi32`, `kernel32`).
- **Based on the Microsoft *V2 Credential Provider Sample*,** stripped to one credential and no editable fields. MIT-licensed sample code → THIRD-PARTY-NOTICES.
- **Compiler and linker hardening:**
  - `/guard:cf`, `/guard:ehcont`, `/CETCOMPAT`, `/DYNAMICBASE`, `/HIGHENTROPYVA`, `/NXCOMPAT`;
  - `/sdl`, `/W4 /WX`, `/Qspectre`;
  - SAL annotations and code analysis (`/analyze`) in CI.
- **`DllMain` does nothing** beyond `DisableThreadLibraryCalls`. No loader-lock work.

### 8.2 Behaviour

- **`SetUsageScenario`:**
  - `CPUS_LOGON` → active;
  - every other scenario (`CPUS_UNLOCK_WORKSTATION`, `CPUS_CREDUI`, `CPUS_CHANGE_PASSWORD`, `CPUS_PLAP`) → `E_NOTIMPL`. The provider then never loads in user processes through CredUI.
- **With no ticket pending, `GetCredentialCount` returns 0.** No tile, no fields, nothing shown, and every other provider works as before. A person at the device can't do anything with it.
- **With a ticket pending:**
  - one tile, "RemoteHub – signing in *DOMAIN\user*…";
  - `pbAutoLogonWithDefault = TRUE`;
  - `GetSerialization` packs `KERB_INTERACTIVE_UNLOCK_LOGON` with `KerbInteractiveLogon` and the Negotiate package (`LsaLookupAuthenticationPackage`), then returns `CPGSR_RETURN_CREDENTIAL_FINISHED`.
- **No password field** (`CPFT_PASSWORD_TEXT`) holds the value, and `GetStringValue` never returns it. The tile has only a label and a status text.
- **Background pipe thread:**
  - Pipe I/O runs on its own thread. It wakes LogonUI through `ICredentialProviderEvents::CredentialsChanged(adviseContext)` and **never blocks a LogonUI call**.
  - `GetSerialization` uses only the ticket already received. If none is there, it returns `CPGSR_NO_CREDENTIAL_NOT_FINISHED`.
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
  - The CLSID is a fixed GUID that never changes, written down here when it is generated, so IT can exclude it.
- **Uninstall,** or an upgrade with `AUTOSIGNIN=0`, removes both keys and the DLL. `Enrollment.Run cleanup` also deletes the keys, in case MSI didn't.
- **Signing:** the DLL **and** `RemoteHubAgent.exe` must be Authenticode-signed (SHA-256, timestamped).
  - **Blocker:** `.github/workflows/release.yml` has no code signing yet ("Code signing goes here once a certificate is available"). The provider must not ship unsigned. Customers' WDAC/App Control and Smart App Control will block it, and an unsigned DLL in LogonUI can't be told apart from a malicious one.
  - The build's existing `build.manifest.sig` should cover the DLL's hash too.
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
- the agent's general behaviour of a remote access tool (screen capture, `SendInput`, Ctrl+Alt+Del from a service).

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

An administrator's desktop is now open on the physical console. Anyone standing at the device can see it and use the keyboard and mouse.

- **Banner:** the existing banner shows (the session has a user now): "Signed in remotely by RemoteHub – *DOMAIN\user*", with Disconnect.
- **When the connection ends,** the session created by automatic sign-in is **locked** (`WTSDisconnectSession` on client SKUs, or `LockWorkStation` from the helper) **or signed out** (`WTSLogoffSession`). New policy `AutoSignInEndAction`:
  - `Lock` (default);
  - `SignOut`.
  - "Ends" means any of: closed by RemoteHub, Disconnect on the device, idle timeout, a refusal, a dropped connection after the resume window (1 min), and service stop.
- **Crash safety:**
  - The service writes the session id + logon time of each automatically signed-in session to `%ProgramData%\RemoteHub\Agent\autosignin.json` (no secrets; ACL SYSTEM + Administrators, same as the rest of the folder).
  - At service start, and when a desktop connection ends, every listed session that still exists and has no live agent connection gets the end action. Then the entry is removed.
- **Resume token:** continuing within a minute keeps the session unlocked (existing behaviour). After that minute, the end action runs.
- **Known limit:** while connected, a person at the device sees and can use the console. There is no privacy mode or input blocking yet (backlog: "privacy mode"). The documentation must say so, and IT should enable the feature only where that's acceptable.

## 10. Handling secrets in .NET (service and RemoteHub)

### 10.1 Service

- **Read `SignInRequest` straight from the WebSocket receive buffer** into a pinned `byte[]` (`GC.AllocateArray(pinned: true)`):
  - never through `DesktopWire.ReadJson`, `Encoding.GetString` or any `string`;
  - zero the receive buffer right after copying.
- **Protect it at once** with `CryptProtectMemory(SAME_PROCESS)`, padded to 16 bytes.
- **Unprotect it only to write it to the pipe,** then zero it. The ticket object is `IDisposable` and zeroes itself in `Dispose` and when it expires.
- **No dumps:** exclude the agent from Windows Error Reporting full dumps (`WerAddExcludedApplication`), or set `DontShowUI` + `LocalDumps\DumpType=0` for `RemoteHubAgent.exe` at install. Document this. The agent already sends no crash reports.
- **Logs:**
  - `AgentLog` never receives the message.
  - Add a test that greps the log and event log text for the test password after a full flow.

### 10.2 RemoteHub

- **Convert just in time:** take the password from the stored credential (already DPAPI + optional startup lock) as `SecureString` → unmanaged buffer (`Marshal.SecureStringToGlobalAllocUnicode`) → copy into the outgoing frame → `ZeroFreeGlobalAllocUnicode` and zero the frame buffer after the send.
- **Send conditions:**
  - only when the agent sent `SignInReady` in *this* connection;
  - the pinned fingerprint matched;
  - the connection's setting is on.
  - Never on reconnect without a new `SignInReady`.
- **Missing password:** if the credential has no password (prompt each time), ask in the existing password dialog. Don't store it.
- **RemoteHub audit log:** "Automatic sign-in requested / succeeded / failed (reason)" for the connection. Never the password.

## 11. Audit

**Agent (log file + Application log, source *RemoteHub Agent*):**

| ID | Level | Text |
|---|---|---|
| 1104 | Information | Automatic sign-in: *account* from *source* signed in to console session *n* |
| 1105 | Information | Automatic sign-in session *n* locked / signed out (reason) |
| 2103 | Warning | Automatic sign-in refused: *account* from *source* – *reason* (policy, right, session state, existing session, rate limit, timeout) |
| 2104 | Warning | Automatic sign-in failed: *account* – Windows reported *NTSTATUS name* |
| 3101 | Error | Automatic sign-in: the signed-in account didn't match – session logged off |
| 3102 | Error | Automatic sign-in unavailable: pipe, registration or signature check failed (detail) |

**Windows:** Security log 4624 with **logon type 2** (interactive) on the device, plus 4648 where applicable. README note for SIEM: an interactive logon of an admin account on a client device means automatic sign-in when a 1104 event comes right before it.

## 12. Phase 2 (not in v1, design kept open)

- **Unlock the operator's own locked session** (`CPUS_UNLOCK_WORKSTATION`). Only if the locked session's user SID equals the authenticated SID, under the same right, policy and ticket. Also needs the "end action = lock again" rule.
- **Privacy mode:** black out the physical screen and block local input while auto-signed-in. This would close T6.
- **RBAC server:** issues `AutoSignIn` in its signed rights token instead of the Windows groups (agent-desktop-draft §4.1).

## 13. Implementation checklist

### Agent (`src/RemoteHub.Agent`)
- [ ] `AgentPolicy`: add `AgentRight.AutoSignIn` (default nobody), `NoUserBehavior` (`SignInScreen` default | `AutoSignIn`) and `AutoSignInEndAction` (`Lock` default | `SignOut`). Include them in `Refused`. Add tests in `AgentDesktopTests`.
- [ ] `ShellIdentity`: carry the user SID and the resolved `DOMAIN\user`.
- [ ] `Desktop/DesktopProtocol.cs`: `DesktopRights.AutoSignIn`, `DesktopFeatures.AutoSignIn`, `DesktopStart.AutoSignIn`, message `SignInRequest` (binary), states `SignInReady` / `SigningIn` / `SignedIn` / `SignInFailed` + reason codes.
- [ ] New `Desktop/AutoSignInBroker.cs`: the pipe (§7.1), peer checks (§7.2), ticket (§7.3), protected memory (§10.1), cancel on every end condition.
- [ ] New `Desktop/AutoSignInSessions.cs`: the end action and crash safety (§9).
- [ ] `DesktopEndpoint`: checks in §6.2, flow in §6.4, `_allowedBy` after a successful sign-in, SID check after sign-in.
- [ ] `SignInGate`: a per-account (SID) bucket for automatic sign-in attempts.
- [ ] `Program.cs`: create the broker before `app.RunAsync()`; run the end action for sessions left over in `autosignin.json`.
- [ ] `AgentLog` / `AuditEvent`: IDs 1104, 1105, 2103, 2104, 3101, 3102.
- [ ] `Enrollment.cs`: `.rhagent` option `autoSignIn`; `cleanup` removes the provider keys and `autosignin.json`.
- [ ] `SelfTest`: report whether the provider is registered and signed and the pipe is up, without triggering a sign-in.

### Credential provider (`src/RemoteHub.CredentialProvider`, new)
- [ ] Project, hardening flags, static CRT (§8.1).
- [ ] `ICredentialProvider`, `ICredentialProviderCredential2`, `ICredentialProviderSetUserArray` (only to return the right user tile).
- [ ] Pipe client thread, server PID and image check, TLV parser (§7, §8.2).
- [ ] `KERB_INTERACTIVE_UNLOCK_LOGON` packing; `ReportResult` → `Result` message.
- [ ] libFuzzer target for the parser; unit tests for packing.

### Installer and build
- [ ] `installer/RemoteHub.Agent.Installer/Package.wxs`: property `AUTOSIGNIN` (Secure); a component with the DLL and the two registry keys, conditioned on it; removed when uninstalling or when set to 0.
- [ ] `build/build-installer.ps1`, `ci.yml`, `release.yml`: build the vcxproj; **Authenticode signing of the DLL and the agent exe (blocker)**; include the DLL in `build.manifest`.
- [ ] Agent install dialog (`AgentInstallDialog.cs`): checkbox "Allow automatic sign-in (installs a credential provider)", off, with a short warning text.

### RemoteHub (`src/RemoteHub`)
- [ ] `RdpOptions`: `AgentAutoSignIn` (bool?, inherited, default false) + the editor UI under *PowerShell remoting* next to *Remote desktop through*. Run the ui-consistency process for the editor and tab changes.
- [ ] `AppPolicy`: `DisableAgentAutoSignIn`.
- [ ] `AgentDesktopSession` / `AgentDesktopView`: the option in the start dialog (only when `Hello.Rights.AutoSignIn`), handle `SignInReady` → send the password once (§10.2), show the `SignInFailed` reasons.
- [ ] `AuditLog`: the new entries.

### Docs
- [ ] `agent-spec.md` §12: move automatic sign-in from backlog to implemented; add the right and the policy values.
- [ ] `agent-desktop-draft.md` §4.4/§4.5/§6: point to this document and replace the `NoUserBehavior` row with the final values.
- [ ] README *Remote desktop through the agent*: the setting, the three switches, the end action, the known limit (§9), the event IDs.

## 14. Tests

**Unit and integration (in CI):**
- Rights and policy matrix: every combination of install, policy, right and client setting. The feature is offered and accepted only when all are on.
- Refusals:
  - locked, disconnected and non-console sessions;
  - a user already signed in;
  - an existing session of the same account;
  - a second pending ticket;
  - a request without `SignInReady`;
  - a request after 30 s;
  - the 4th attempt within 15 min.
- Ticket:
  - single use;
  - expiry;
  - wrong nonce or session;
  - cancelled on disconnect, session change or policy change.
- Pipe:
  - a non-SYSTEM client is refused;
  - a client in a different session is refused;
  - a non-LogonUI image is refused;
  - squatting: the pipe is created first by a test process → the feature reports off;
  - oversize and malformed messages.
- Secrets: after a full fake flow, the test password doesn't appear in the log file, the event log text, or a managed heap snapshot (`ClrMD`) of the service.
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
  - `DisableCAD = 0`.
- Entra ID and hybrid joined devices (not proven yet):
  - Entra ID joined, cloud-only account (`AzureAD\user@tenant`): first check the agent's own sign-in, which uses a network logon.
  - Entra ID joined: the provider's password sign-in through Negotiate/CloudAP, the first sign-in on the device without internet, the passwordless policy, and Windows Hello for Business being required.
  - Hybrid joined with an on-premises account: expected to behave like a domain-joined device.
- Physical-console behaviour:
  - banner;
  - lock / sign-out on Disconnect, idle timeout, network drop after 1 min, killing the service, and a reboot during a session.
- Robustness: the provider registered while the service is stopped and while the pipe is flooded → the normal sign-in screen and Windows Hello PIN still work.
- `ExcludedCredentialProviders` with our CLSID → the provider is gone and the agent reports the feature off.

## 15. Security review – questions for the reviewer

1. Is SYSTEM + LogonUI image + session id enough to identify the provider at the pipe? An alternative is an ALPC port or an RPC interface with `RPC_IF_ALLOW_LOCAL_ONLY` and a security callback.
2. Is `Lock` the right default end action, or should `SignOut` be the default, so no admin profile stays loaded on shared devices?
3. Should the right be limited to non-built-in accounts (e.g. refuse RID 500), or to members of a dedicated group only?
4. Is re-sending the password (§6.1) better than keeping it protected from the Basic sign-in for the connection's lifetime?
5. Does the residual risk T6 (a person at the console during the session) need privacy mode before release, rather than in phase 2?
