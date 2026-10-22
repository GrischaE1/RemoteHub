# RemoteHub Agent – Automatic sign-in (credential provider)

Status: v3, brought in line with the implementation in 0.18.11 (2026-10-15). Phases 0–4 are built (§13); what is still open is listed in §16. All questions in §15 are decided (Q5 changed, Q7–Q9 new, 2026-10-14). The separate security review asked for in [agent-desktop-draft.md](agent-desktop-draft.md) §4.4 is still to come. Earlier drafts: v3 (2026-10-14) after the design review, checked against 0.18.8 with privacy mode ([privacy-mode-design.md](privacy-mode-design.md), 0.18.3) and jump hosts ([jump-host-design.md](jump-host-design.md)).
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
| T1 | On the network, no credentials | Trigger a sign-in, or get passwords | Pinned TLS, Windows sign-in, new right `AutoSignIn` (default: the allowed groups, like the other rights; IT narrows or turns it off by policy), attempt limit per account (§5, §6) |
| T2 | Operator with a desktop right but not `AutoSignIn`, or connected view-only | Sign in without the right, or gain control through the sign-in | The rights and the view-only flag are checked in the service, not trusted from RemoteHub (§6.2) |
| T3 | Operator with `AutoSignIn` | Sign in as **another** account, or into an occupied session | Only the account that authenticated the connection (same SID); console must be in the `SignIn` state when the ticket is claimed; never ends or takes over a session (§6.3) |
| T4 | Local user without admin rights on the device | Inject credentials into the provider, read them, or squat the pipe | Pipe owned by the service and created at start (`FIRST_PIPE_INSTANCE`), SYSTEM-only DACL, remote clients rejected, both ends check the peer process, one-shot ticket (§7) |
| T5 | Local user / malware without admin rights | Replace or hijack the provider DLL | Program Files ACLs, signed DLL, no DLL dependencies outside System32, fixed CLSID under HKLM (§8.4) |
| T6 | Person at the device | Use the admin session that was just signed in | Privacy screen with local input blocked, on from the start and kept through a dropped connection (§9.1, §9.2); no screen snips while it is up (§9.1); the end action when the connection ends, and **sign-out** 2 minutes after a drop, including after a crash (§9.2, §9.3) |
| T7 | Memory disclosure (dump, crash report, pagefile) | Read the password | Password kept only in pinned buffers, protected with `CryptProtectMemory`, zeroed after use, never in a .NET `string`, never in logs (§10). Plaintext left in the TLS and Kestrel buffer pools is the same as for the Basic sign-in today and is accepted (§10.1). |
| T8 | Compromised RemoteHub PC | — | Out of scope: it already has the stored passwords. Automatic sign-in adds nothing for it. |
| T9 | Local admin or SYSTEM on the device | — | Out of scope: they already control the device. |
| T10 | Bug or crash in the provider | Sign-in screen broken for everyone (denial of service) | Minimal native code, no blocking calls on LogonUI threads, input fuzzed, inert unless a ticket is pending, can be left out at install (`AUTOSIGNIN=0`), policy kill switch (§8) |
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

## 5. Enabling: four switches, on by default, each with a policy to turn it off

Automatic sign-in works only if **all** of these are on. Like every RemoteHub feature it is available by default, and admins switch it off through the ADMX templates ([admx-design.md](admx-design.md)) – each layer has its own policy.

| Layer | Switch | Default | Turned off by |
|---|---|---|---|
| Install | MSI feature `CredentialProvider` (§8.4), in agent MSIs built with `build-installer.ps1 -CredentialProvider` – every signed release. `AUTOSIGNIN=0` leaves it out; RemoteHub's install dialog (*Allow automatic sign-in (installs a credential provider)*) and right-click entry set it. | installed | `AUTOSIGNIN=0`; console policies `AgentInstall\AutoSignIn` = 0 (install dialog unticked, so silent installs pass `AUTOSIGNIN=0`) and `AllowCredentialProviderInstall` = 0; on the device Windows' *Exclude credential providers* with our CLSID. |
| Device policy | `HKLM\SOFTWARE\Policies\RemoteHub\Agent` `NoUserBehavior` (REG_SZ), else `agent.json` `noUserBehavior`. Not set anywhere → `AutoSignIn`. `SignInScreen`, an unknown word, or an unreadable key → off. | allowed | Policy *When nobody is signed in* = *Show the sign-in screen*. Read for every request. |
| Right | `Rights\AutoSignIn` (REG_MULTI_SZ, group SIDs), else `agent.json` `autoSignInGroups`, else the groups every other right defaults to (policy `AllowedGroups`, else the install's allowed groups – local Administrators unless chosen otherwise). | allowed groups | Policy *Who may sign in automatically* with other groups, or `S-1-0-0` for nobody. Separate from `SecureDesktop`, so IT can allow the sign-in screen without automatic sign-in. |
| Client | Per connection: *Sign in automatically when nobody is signed in* (inherited like the other agent settings). The connect screen shows *Sign in automatically as …* whenever the device offers it; the connection's setting only decides whether it starts ticked. | offered, unticked | Console policy `DisableAgentAutoSignIn` (DWORD 1) in `HKLM/HKCU\SOFTWARE\Policies\RemoteHub`, or `Connections\AgentAutoSignIn`. RemoteHub never sends the password for this unasked. |

**Prerequisites, not switches** (decided, §15 Q9): the account must also hold the `Desktop` right (control) and the `SecureDesktop` right, and the connection must not be view-only. That's the §1.1 baseline: without them the operator couldn't type the password into the sign-in screen either.

**Optional device policy** (decided, §15 Q7): `AutoSignInRequirePrivacyScreen` (DWORD, default 0). With 1, automatic sign-in is off on a device that can't show the privacy screen (policy `PrivacyScreen` = 0, or Windows build < 19041) (§9.1).

- **Set at install, or by IT:** the policy and the right come from Group Policy / Intune, else from `agent.json`, else the defaults above.
  - The install's choice is kept in `agent.json` (since 0.18.12; [admx-design.md](admx-design.md) §11.3). `AUTOSIGNIN=1` and RemoteHub's *Install credential provider* run `RemoteHubAgent.exe auto-sign-in on`, which writes `noUserBehavior = AutoSignIn` and `autoSignInGroups` = the agent's allowed groups. `AUTOSIGNIN=0`, *Remove credential provider* and uninstall (`cleanup`) run `auto-sign-in off`, which writes `noUserBehavior = SignInScreen` – explicitly, because not set means allowed.
  - No `AUTOSIGNIN` (a hand-run MSI, an ordinary update) leaves `agent.json` as it is; a first install then has the defaults: allowed.
  - The agent never writes the policy key, so IT's policy always wins.
  - The MSI writes `agent.json` only through `AgentEnroll`, which runs when the product itself is installed or updated. A hand-run `msiexec … ADDLOCAL=CredentialProvider` / `REMOVE=CredentialProvider` on an installed agent changes the provider only; RemoteHub's right-click entry runs `auto-sign-in on|off` itself after msiexec (§8.4).
  - **Gap:** `AgentEnroll` passes `auto-sign-in=[AUTOSIGNIN]` in every package, so `AUTOSIGNIN=1` on an MSI built without the provider still writes `agent.json`, with no provider to use it. Harmless (Hello reports `AutoSignIn` false, "the credential provider isn't installed").
  - The install dialog's option applies only to the silent ways (WinRM, through the agent). The other ways run the MSI by hand, which installs the provider unless `AUTOSIGNIN=0` is added to its command line.
- **Kill switch:** Windows' own policy *Exclude credential providers* (`ExcludedCredentialProviders`) with our CLSID disables the provider on the device without uninstalling.
- **Policy cannot be read:** fail closed. `AgentPolicy.Refused` turns everything off: its `NoUserBehavior` is `SignInScreen` and `Rights\AutoSignIn` is nobody, so automatic sign-in is off too – the "allowed by default" applies only to a key that was read and doesn't set the value.

## 6. Protocol and service-side checks

### 6.1 Protocol changes (`Desktop/DesktopProtocol.cs`, protocol version stays 1, feature-flagged)

- `DesktopRights` is `DesktopRights(Control, SecureDesktop, ConsentBypass, PrivacyScreen = false, PrivacyInputBlock = false)` today. It gets `bool AutoSignIn = false` at the end, so older RemoteHub builds still read it.
  - It's true only if the policy is on, the right is granted, `Control` and `SecureDesktop` are true, the provider is registered and signed, and the pipe is up. With `AutoSignInRequirePrivacyScreen` = 1, the privacy screen must also be available.
  - RemoteHub offers the option only when it's true.
- New feature string `DesktopFeatures.AutoSignIn = "autoSignIn"` in Hello. It's added like `DesktopFeatures.PrivacyScreen`, only when the device can do it.
- `DesktopStart` gets `bool AutoSignIn`. It's the operator's intent, and it's valid only when the selected session is the console in state `SignIn` and `ViewOnly` is false.
- `DesktopStart` also gets `string? AutoSignInEndAction` (`Lock` | `SignOut`), the connection's setting (§9.2). The device policy wins when it's set; anything else, or nothing, means `Lock`.
- New message `SignInRequest` (RemoteHub → agent):
  - binary, **not JSON**: `[type = 31][u16 len][UTF-16LE password bytes]`, the password at most 256 characters (`DesktopWire.MaxSignInPasswordChars`);
  - no user name, because the agent uses the account that authenticated (§6.3);
  - sent only after the agent asks for it with status `SignInReady`. RemoteHub never sends it on its own.
- New status states:
  - `SignInReady`: the agent is ready; RemoteHub now sends `SignInRequest`.
  - `SigningIn`.
  - `SignedIn`: the provider reported success and the new session's SID matched (§6.3). It carries the end action (`EndAction`) that applies.
  - `SignInFailed`: carries a readable message; the agent's audit carries the reason code (`AutoSignInFailure`: `notAllowed`, `unavailable`, `sessionState`, `existingSession`, `busy`, `rateLimit`, `timeout`, `noProvider`, `sessionChanged`, `windows`, `wrongAccount`). Windows' refusals are described from the NTSTATUS (`AutoSignInStatus`: wrong password, account doesn't exist, locked, password expired, disabled, account expired, "Allow log on locally", time of day, workstation, smart card / Windows Hello needed, authentication firewall; anything else "Windows refused the sign-in (0x…)"). The message never echoes input.
- `AgentPolicy.Refused` includes `AutoSignIn` = nobody; the defaults give it to the allowed groups (§5).

> Why the password is sent a second time instead of keeping it from the Basic header (decided, §15 Q4): the agent doesn't keep the Basic sign-in password after `LogonUser`. Keeping it for every connection "just in case" would put every operator's password in memory for the whole session. Sending it again only on explicit request keeps the exposure to a few seconds, and only for connections that use the feature.
>
> Baseline to keep in mind: the Basic sign-in already brings the same password into the service as a .NET `string` on every connection (`AgentHost` `TryBasic` → `WindowsAuthenticator.Authenticate` → `LogonUser` with `LOGON32_LOGON_NETWORK`), which can't be zeroed. The careful handling of `SignInRequest` (§10.1) keeps the *additional* copy short-lived; it doesn't lower exposure below that baseline.

### 6.2 Checks in `DesktopEndpoint` (all in the service; RemoteHub's word is never trusted)

The service checks these **in order**. Any failure → `SignInFailed`, audit 2104 (2105 for a refusal by Windows), and the connection continues on the plain sign-in screen.

Checks 1–3 also decide Hello's `DesktopRights.AutoSignIn` (`AutoSignInRefusal`). When the device's policy allows automatic sign-in but it isn't offered while the console shows the sign-in screen, the agent logs why ("Desktop: automatic sign-in not offered to *account*: …", log only, no event). Checks 1–5 run at `Start`; the sign-in itself starts only after the desktop connection is `Connected` (after `Begin`).

1. `policy.NoUserBehavior == AutoSignIn` ("Automatic sign-in is turned off on this device (policy NoUserBehavior).") and `identity.InAny(policy.Groups(AgentRight.AutoSignIn))` ("… may not sign in automatically on this device (right AutoSignIn)."), read again at request time. With `AutoSignInRequirePrivacyScreen` = 1, the privacy screen must also be available (`PrivacyScreenSupported` and policy `PrivacyScreen` ≠ 0). The account's SID must be known.
2. The connection is a control connection (`Desktop` right, `ViewOnly` false) and the account holds `SecureDesktop` ("Automatic sign-in needs the rights Desktop and SecureDesktop on this device."; view-only: "Automatic sign-in isn't available in a view-only connection."). Without `SecureDesktop` the `Start` on the sign-in screen is refused before any automatic sign-in check, like any start there: `Failed` "Nobody is signed in or the session is locked – and your account may not see the sign-in screen on this device." (2100).
3. The provider is usable (`AutoSignInBroker.Unavailable`, checked at most once a minute): the broker pipe was created at service start; the `Credential Providers\{CLSID}` key exists; `InprocServer32` points to `RemoteHubCredentialProvider.dll` in the agent's own folder; `ExcludedCredentialProviders` doesn't name the CLSID; the DLL exists, its Authenticode signature chains to a trusted root, and it is signed by **the same certificate as the agent's exe** (§8.4).
4. Session checks:
   - the selected session is the **current console session** (`WTSGetActiveConsoleSessionId`);
   - its state is **`SignIn`**: no user, `LogonUI` running in it;
   - it's not `Locked` or `Disconnected`, and no user is signed in on the console.
   - Use the agent's existing session detection (`DesktopServices`, the `LogonUI` check since 0.15.11). On Windows 11 and Server 2025 the "Press Ctrl+Alt+Delete" screen runs on the **Default** desktop, not Winlogon's, so the desktop name alone isn't a reliable signal.
   - Texts: "Automatic sign-in works only on the device's console." / "Someone is signed in on the console now – automatic sign-in only works when nobody is." (`sessionState`).
5. The authenticated account has **no other session on the device** (`WTSEnumerateSessions`, compared by SID). On client Windows, signing in on the console would take over an existing RDP session of that account and disconnect the person using it. → Refused with "This account already has a session on the device – connect to that session instead." (`existingSession`).
6. Only one sign-in at a time per device (§7.3). A second one while one is running → "Another automatic sign-in is running on this device." (`busy`).
7. At most one attempt per desktop connection (a second `SignInRequest` is a protocol error and ends the connection). At most 3 attempts per account per 15 minutes, counted when the password arrives: the 4th → "Too many automatic sign-ins for this account – try again in 15 minutes, or sign in at the sign-in screen." (`rateLimit`). This protects against account lockout when the stored password is out of date.
   - The counter is a **new, small class** (`AutoSignInGate`), keyed by the account SID, not by source address, for the whole device (in memory: a service restart resets it). The existing `SignInGate` (`AgentHost.cs`) counts failures and parallel attempts per source address for the Basic sign-in. Through a jump host every operator has the jump host's address as their source, so a per-source limit would let one operator's bad password block everyone else.
8. The `SignInRequest` arrives within 30 s of `SignInReady`. Otherwise: "RemoteHub didn't send the sign-in in time." (`timeout`).

**Checked again when the ticket is claimed (§7.3):** the policy and right of check 1 (not `AutoSignInRequirePrivacyScreen`) and checks 4 and 5 run once more when the provider's `Claim` arrives, before the ticket is handed out (`sessionChanged`, "Automatic sign-in was turned off on this device meanwhile." or the check's own text). That can be up to a minute after `SignInRequest`, and someone at the device may have signed in in the meantime. The ticket isn't cancelled actively on a state change; this re-check is what catches it. The broker also runs it before `Pending`: when someone has signed in at the device meanwhile, the LogonUI it still holds has gone, and writing to it failed with "The connection to the credential provider broke: Pipe is broken." instead of saying why (lab C-AUTO-15). A pipe that breaks before the ticket goes out is reported as `sessionChanged` when the re-check fails then, else as `noProvider`.

### 6.3 Which account – exactly the authenticated one

- `WindowsAuthenticator.Authenticate` already holds the logon token. Keep the account **SID** in `ShellIdentity` as its own property (`UserSid`). Today it's only mixed into `GroupSids` (`WindowsShell.cs` appends `identity.User` to the group list).
- Keep the name as Windows resolved it, `DOMAIN\user`, which `ShellIdentity.Account` already holds (`WindowsIdentity.Name`). For local accounts it's `COMPUTERNAME\user`.
- The ticket carries that domain and user. The client can't choose it.
- After the provider reports success, the service reads the new console session's user SID (`WTSQueryUserToken` → `TokenUser`, tried for up to 30 s). If it doesn't match: **log off that session immediately** (`WTSLogoffSession`), audit error 3101, and end the connection (`Ended` "The automatic sign-in couldn't be confirmed, so it was undone. Details are in the agent's log."). This is a defence against bugs and races; it should never happen.
- **When Windows' answer is lost** (lab C-END-10): the ticket went out, but no `Result` came back – the pipe ended or broke, or the 50 s or the ticket's life ran out. LogonUI may have signed the account in all the same (it releases the provider right after a successful sign-in, and its process can end before the provider's thread has sent the result). The broker then marks its failure as unconfirmed (`AutoSignInResult.Unconfirmed`), and the service reads the console session's user SID as above, for up to 30 s. **Fail safe:** if it is the account's, the sign-in is treated as successful – recorded in `autosignin.json`, covered (§9.1), `SignedIn` with the end action (§9.2), 1106 with "Windows' answer was lost, the console session confirmed it" – because only this ticket can have signed that account in on the console now (check 5 of §6.2 ruled out another session of it). Otherwise it is the failure the broker reported (`timeout` or `noProvider`), the service's privacy screen is dropped, and nobody is signed out: a session of another account wasn't created by this ticket.
- **When the connection ends during the sign-in:** before the ticket goes out, the sign-in simply stops (no ticket is sent). After it, the RemoteHub connection no longer cuts anything short: the broker still waits for the `Result` within the ticket's life, and the console check above runs as well. If the account turns out to be signed in after the connection's end has already run, the sign-in gives the session its end action itself, as that end would have (1106, then 1107): a connection ended on purpose → the end action; a dropped one → signed out at once, because no helper covers the new session ("connection lost, nothing to cover the screen", §9.2). A lock makes sure exactly one of the two – the connection's end or the sign-in – applies it.
- **Consent after the sign-in is decided by SID.** `IsSelfSession` compares the session's user SID with the account's where Windows reports it, and falls back to the name (`IsSelf`) only where it doesn't, so a name written another way (UPN, short domain name) can't lead to a different decision. `_allowedBy` is then set from the session that passed this check.
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

- **Order in the code:** the desktop connection is started and `Connected` first (consent and `Begin` as for any start on the sign-in screen); then `SignInReady`. After the password arrives: the attempt counter (check 7), `SigningIn`, the privacy screen (§9.1), then the broker. `autosignin.json` is written only after the SID check, and `SignedIn` follows.
- **Ctrl+Alt+Del:** since 0.15.7 the agent sets `SoftwareSASGeneration = 1` for as long as it's installed (marker `sas.agent`), so the service's `SendSAS` is accepted without a policy change at sign-in time. The broker waits 3 s for the provider to connect; if it hasn't (the sign-in screen waits for Ctrl+Alt+Del or shows the lock picture), it sends Ctrl+Alt+Del and waits up to 20 s more. No provider by then → `noProvider` "The device's sign-in screen didn't load the RemoteHub credential provider."
- **Time limits:** the ticket lives 60 s from the start of the broker's work; the `Claim` must come within 10 s of `Pending` (else `noProvider` "The credential provider didn't take the sign-in."), the `Result` within 50 s of the `Ticket` (else `timeout` "The sign-in screen didn't take the sign-in. If it shows a message (for example after a failed attempt), click OK and try again."). A broken pipe → `noProvider` "The connection to the credential provider broke: …". Once the ticket has gone out, none of these is final until the console has been checked (§6.3, "When Windows' answer is lost"). **`NotOffered` instead of a `Result`** (§7.3, §8.2: the sign-in screen doesn't list the account and didn't take the ticket) is final at once – the ticket was never used, so there is no console check. The service asks Windows itself: `LogonUserW` with the ticket's account and password, `LOGON32_LOGON_INTERACTIVE` (the password in a pinned buffer for the call only, zeroed right after; a token is closed at once). Its error is worded like the NTSTATUS LogonUI would have reported (1385 → "Allow log on locally", 1331 disabled, 1909 locked, 1793 account expired, 1907/1330 password expired, 1326 wrong password …); success means Windows would take the account and the sign-in screen just doesn't list it yet (a new account), which is what the operator is told. `notOffered`, audit 2105 "… the sign-in screen didn't offer the account; checked with Windows: error N (0x…)". **The check is one more interactive sign-in attempt** for the account: it shows in the Security log, and with a wrong password it counts towards the lockout threshold like any attempt (the rate limit of §6.2 check 7 still applies per RemoteHub attempt, so at most two attempts count per RemoteHub sign-in).
- **Consent after sign-in:** the new session belongs to the operator's own account. The existing code continues without asking when the account that signed in is the operator's own (audit 1102 "… signed in to session *n* with their own account – continued without asking"), and `HoldUserDesktop` releases the picture for this user only. The decision uses the SID (§6.3).
  - If anyone else's desktop shows up (a race, fast user switching), the existing ask/PIN rule applies unchanged.

## 7. Local channel: service ⇄ provider

### 7.1 The pipe

- **Name:** `\\.\pipe\RemoteHubAgent.AutoSignIn`.
- **Created when the service starts and before it listens on the port:**
  - The first instance is created with `FILE_FLAG_FIRST_PIPE_INSTANCE`, so nobody can create it first (pipe squatting). If that fails, the feature is off until the service restarts, and that is logged (3102).
  - **Instances:** `nMaxInstances` = 4. After a client connects, the service creates the next instance at once, so one connected or stuck client never leaves the console's LogonUI without a free instance. A client that fails the peer check (§7.2) is disconnected immediately and its instance is reused. That is logged ("Automatic sign-in: pipe client refused (*why*)"), not audited.
  - The pipe is created at every service start, whether the provider is installed or not, so the provider can be added to a running agent.
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
  - an expiry of 60 s after `SignInRequest` (from when the broker starts on it).
- **Only one at a time:** the broker takes one sign-in per device; a second one fails at once (`busy`, §6.2 check 6).
- **Checked again at `Claim`:** §6.2 checks 1 (policy and right), 4 and 5. If any fails, the ticket is deleted without being sent, and the operator gets `SignInFailed` with the reason (`sessionChanged`).
- **Cancelled when:** it expires, the RemoteHub connection ends, or the service stops. A console session that changes or leaves the `SignIn` state, or a policy turned off, isn't watched while the ticket waits; the re-check at `Claim` refuses it.
- **Messages:** `Pending(nonce, session)` → `Claim(nonce, session)` → `Ticket(nonce, domain, user, SID, password)` → `Result(nonce, NTSTATUS, substatus)`, or instead of the `Result` `NotOffered(nonce)`: the sign-in screen doesn't list the account and didn't take the ticket within the provider's grace; the ticket is wiped unused (§8.2). No other message types exist. The provider can't ask for anything else, and the service never sends a ticket without a `Claim`.
- **Wire format** (`src/RemoteHub.CredentialProvider/Wire.h`): `[u8 version = 1][u8 type][u16 payload length][payload]`, little-endian, one message per pipe message. Strings are a `u16` length in UTF-16 units followed by the units: names at most 256, the password at most 256. A message that doesn't parse exactly (wrong version or type, a length that doesn't match, bytes left over, an empty user or password) closes the connection. A parsed message written back is byte-for-byte the same, which the fuzzer checks.

## 8. The credential provider (new native project)

### 8.1 Project

- **`src/RemoteHub.CredentialProvider`:**
  - C++20, `vcxproj`, x64 (plus ARM64 if the agent ever ships ARM64);
  - static CRT (`/MT`);
  - no dependencies beyond Windows system DLLs (`secur32`, `crypt32`, `advapi32`, `ole32`, `kernel32`).
- **Written from scratch** following the structure of Microsoft's *V2 Credential Provider Sample*: one credential and no editable fields. No sample code is copied, so nothing goes into THIRD-PARTY-NOTICES.
- **Configurations:** `Release` is the only DLL that goes into the MSI. `Lab` (`RemoteHubCredentialProvider.Lab.dll`, define `RHCP_LAB_SERVER`) accepts the phase 1 test server `RemoteHubCpTestServer.exe` from the same folder instead of the agent service. It's built only by `build-credential-provider.ps1 -Configuration Lab`, used only on lab VMs, and never packaged.
- **Build:** `build/build-credential-provider.ps1` (MSBuild from the Visual Studio C++ tools, found through vswhere; not part of `RemoteHub.sln`, which `dotnet build` can't build with C++ in it). It runs the unit tests in `tests/RemoteHub.CredentialProvider.Tests` and, with `-FuzzSeconds`, the libFuzzer target (configuration `Fuzz`, `/fsanitize=fuzzer,address`). `/Qspectre` needs the Spectre-mitigated libraries (component `Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre`).
- **The server check without extra paths:** the provider expects the agent's exe **next to its own DLL** (both in `Program Files\RemoteHub Agent`), so it needs neither the registry nor a known-folder lookup to find it.
- **Compiler and linker hardening:**
  - `/guard:cf`, `/guard:ehcont`, `/CETCOMPAT`, `/DYNAMICBASE`, `/HIGHENTROPYVA`, `/NXCOMPAT`;
  - `/sdl`, `/W4 /WX`, `/Qspectre`;
  - code analysis (`/analyze`) in CI is not set up yet (§16).
- **`DllMain` does nothing** beyond `DisableThreadLibraryCalls`. No loader-lock work.

### 8.2 Behaviour

- **`SetUsageScenario`:**
  - `CPUS_LOGON` → active, but only in the console session (§7.2). In any other session it behaves as if no ticket is ever pending.
  - every other scenario (`CPUS_UNLOCK_WORKSTATION`, `CPUS_CREDUI`, `CPUS_CHANGE_PASSWORD`, `CPUS_PLAP`) → `E_NOTIMPL`. The provider then never loads in user processes through CredUI.
- **With no ticket pending, `GetCredentialCount` returns 0.** No tile, no fields, nothing shown, and every other provider works as before. A person at the device can't do anything with it.
- **With a ticket pending:**
  - one tile, labelled "RemoteHub", with the status text "Signing in *DOMAIN\user*…";
  - `pbAutoLogonWithDefault = TRUE`;
  - `GetSerialization` packs `KERB_INTERACTIVE_UNLOCK_LOGON` with `KerbInteractiveLogon` and the Negotiate package (`LsaLookupAuthenticationPackage`), then returns `CPGSR_RETURN_CREDENTIAL_FINISHED`.
- **No password field** (`CPFT_PASSWORD_TEXT`) holds the value, and `GetStringValue` never returns it. The tile has only a label and a status text.
- **Background pipe thread:**
  - Pipe I/O runs on its own thread, and **no LogonUI call ever waits for it**.
  - On `Pending` the thread sends `Claim` at once and keeps the `Ticket` that comes back protected. Only then does it wake LogonUI through `ICredentialProviderEvents::CredentialsChanged(adviseContext)`, so the tile appears only when the ticket is already there. If LogonUI hasn't called `Advise` yet, that call goes nowhere: once it advises, the thread (never `Advise` itself) sends it again while the ticket is unused (`NotifyOnAdvise`) – Server 2025 enumerated after `Advise`, showed the tile with auto sign-in and never asked for it otherwise (lab C-PROV-03).
  - `GetSerialization` uses only the ticket already received. If none is there, it returns `CPGSR_NO_CREDENTIAL_NOT_FINISHED`.
  - The `Ticket` message also carries the account's SID, for `ICredentialProviderCredential2::GetUserSid`. Without one, the tile goes under "Other user".
  - **An account LogonUI doesn't list (lab C-AUTO-20).** Windows 11 leaves locked-out and disabled accounts out of the user array it hands over (`ICredentialProviderSetUserArray::SetUserArray`), and LogonUI never signs in a tile for them – neither under the account's SID nor under "Other user" (both tried; the trace of §8.6 showed no `GetSerialization`), so Windows' refusal never came and the service ran into its 50 s result timeout. Windows Server 2025 reported all four refusals, and it signs in the SID tile even of an account it may not list (C-PROV-03 failed when that tile went under "Other user"). So the tile stays as it is – SID and all – and the provider keeps the array only to compare: when an array is there and lacks the ticket's SID (`UserList.h`, checked on every `GetCredentialCount`), the pipe thread gives LogonUI 10 s to take the ticket. A ticket LogonUI uses it takes within milliseconds of the enumeration. If it hasn't by then, the thread wipes the ticket, sends `NotOffered` instead of a `Result`, and tells LogonUI the tile is gone. Without an array, or with the account in it, nothing changes. The service then asks Windows itself for the reason (§6.4).
- **`ReportResult`:**
  - sends the NTSTATUS and substatus to the service. LogonUI releases the provider right after a successful sign-in, so the stop that follows must not lose the result: the pipe thread serves a reported result before a stop (`WaitServe`), and the stop doesn't cut the result's write short – only the write's own 5 s limit does (lab C-END-10, where the stop won and the account was signed in without the service knowing). If LogonUI's process ends before the thread has sent it, the service still finds out from the console (§6.3);
  - clears the ticket – after a failure (or a `NotOffered`) also telling LogonUI the tile is gone (`CredentialsChanged`, `TileGoesAfterResult`), so it doesn't stay selected with "Signing in …" over the password tiles (lab C-END-13, where a password typed by hand went to it); after a success LogonUI releases the provider anyway;
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
- **Registration in the agent MSI** (component in the feature `CredentialProvider`, below), with WiX `RegistryKey`, not `regsvr32`:
  - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{CLSID}` (default = "RemoteHub Agent");
  - `HKCR\CLSID\{CLSID}\InprocServer32` (default = full path, `ThreadingModel = Apartment`).
  - The CLSID is a fixed GUID that never changes, so IT can exclude it: **`{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}`**.
- **Its own MSI feature, `CredentialProvider`** ("Automatic sign-in (credential provider)"), present only in MSIs built with `build-installer.ps1 -CredentialProvider`, and installed by default (level 1), like every feature. `AUTOSIGNIN=0` sets its level to 0, which rules it out. The release workflow passes `-CredentialProvider` whenever its signing step can sign (step output `enabled`); without signing the release has no provider, because it is never built unsigned.
- **Upgrades** (decided, §15 Q6), the same rule as `VIRTUALDISPLAY`:
  - `AUTOSIGNIN=1` installs or keeps the provider; `AUTOSIGNIN=0` removes it;
  - no value (an ordinary update) **keeps what's installed**. Windows Installer carries the feature's state over to the new version (`MigrateFeatureStates`), so nothing has to be remembered.
  - An update from a package without the feature (an unsigned release) installs it, as a first install would: the old version had no state to carry over. `agent.json` is left as it is, so automatic sign-in is allowed unless the install had turned it off (§5).
- **Adding or removing it on an installed agent** (RemoteHub: right-click → *Install credential provider* / *Remove credential provider*, `AgentInstaller.ChangeCredentialProvider` over the agent's PowerShell):
  - **Adding:** `msiexec /i <package> ADDLOCAL=CredentialProvider`. RemoteHub uploads its own agent package, because Windows' cached copy of the MSI has no files in it. This only works when the device runs the **same build** (same ProductCode). Any other version would be an update, so RemoteHub refuses with "Update the agent first".
  - **Removing:** `msiexec /i {ProductCode} REMOVE=CredentialProvider`, which needs nothing uploaded.
  - **Both are maintenance installs:** the service isn't stopped and its enrollment is untouched. `AgentEnroll` runs only when the product itself is installed (`NOT Installed`), so msiexec alone doesn't touch the policy values. RemoteHub's script therefore runs `RemoteHubAgent.exe auto-sign-in on|off` after msiexec (§5) and shows its answer ("Automatic sign-in allowed on this device for Administrators." or what was left as IT set it).
  - Still only the signed MSI registers the provider (§8.5).
  - RemoteHub remembers what it last did (`AgentDevice.CredentialProvider`) to choose the menu entry, and audits it (`agent.credential_provider`).
  - Exit code 3010 (the sign-in screen has the DLL loaded) is reported as "after the device restarts".
  - The entry (right-click → *RemoteHub Agent* → *Install credential provider* / *Remove credential provider*) shows only for an installed agent and only when RemoteHub's agent package contains the feature (`AgentInstaller.PackageHasCredentialProvider`): every signed release, and lab builds. An unsigned release has no provider, so the entry and the install dialog's option don't show.
- **Uninstall,** or an upgrade with `AUTOSIGNIN=0`, removes both keys and the DLL (the MSI's `ForceDeleteOnUninstall`). `cleanup` at uninstall deletes `autosignin.json` and runs `auto-sign-in off`; it doesn't delete the provider keys itself (§16).
- **Through a jump host:** installing the agent through a jump host (supported since jump hosts) passes `AUTOSIGNIN` the same way. Nothing in the provider depends on the route.
- **Signing:** the DLL **and** `RemoteHubAgent.exe` must be Authenticode-signed (SHA-256, timestamped).
  - **Release signing:** SSL.com eSigner (cloud key, 240 signings a year) through CodeSignTool, in `release.yml` only. A release waits for an approval in the GitHub environment `release-signing` and signs 4 files, 5 with the provider; test releases (a tag suffix such as `v0.19.1-test.1`) and CI are never signed. The provider must not ship unsigned: customers' WDAC/App Control and Smart App Control will block it, and an unsigned DLL in LogonUI can't be told apart from a malicious one.
  - RemoteHub has no signed build manifest of its own. (`build.manifest` / `build.manifest.sig` in the publish folder belong to Microsoft's PowerShell.Native package.) Integrity of updates comes from the SHA-256 of the MSI in the release metadata, which `UpdateService` checks. The DLL ships inside the agent MSI and is covered that way, and by its Authenticode signature.
- **Until there's a certificate (lab only):**
  - Sign the DLL and the agent with a self-signed code-signing certificate that only the lab VMs trust: LocalMachine `Root` + `TrustedPublisher`.
  - That way the signature check below runs unchanged and gets tested too.
  - There's no switch or registry value to skip the check. Such a value would also work on customer devices.
  - The release build fails if the DLL isn't signed by the release certificate.
- The service checks the DLL's Authenticode signature (trusted chain, no revocation check because the device may be offline, and the same signing certificate as `RemoteHubAgent.exe`), and that the registry's `InprocServer32` path points to it, before it offers `AutoSignIn` in Hello (§6.2 check 3). If a check fails → off; the reason is in the agent's log ("not offered") and in the 2104 of a refused request ("*DLL* isn't signed by the same certificate as the agent", …).

### 8.5 Antivirus and EDR

`LogonUI.exe` isn't replaced or changed. It loads the DLL through the documented credential provider API, like Duo, Okta, RSA and Cisco do.

Security tools may still alert, because registering a credential provider is a known attacker technique (MITRE ATT&CK T1556, *Modify Authentication Process*). Typical triggers:
- a write to `…\Authentication\Credential Providers\{CLSID}`, which is a common Sysmon/EDR rule;
- a new DLL with little reputation loaded into LogonUI;
- the agent's general behaviour of a remote access tool (screen capture, `SendInput`, Ctrl+Alt+Del from a service, low-level input hooks for the privacy screen, and tunnelling on a jump host).

Measures:
- **Left out where not wanted:** `AUTOSIGNIN=0` (or the console policy `AgentInstall\AutoSignIn` = 0 for RemoteHub's silent installs) keeps the provider and its registry write off a device; Windows' *Exclude credential providers* turns an installed one off.
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

### 8.6 Trace

The service sees only what comes through the pipe. When Windows' answer never comes, it can't tell whether LogonUI didn't use the tile, didn't ask for the serialization, or didn't call `ReportResult` – lab C-AUTO-20 on Windows 11 26H2, where a locked or a disabled account ran into the 50 s result timeout while "Deny log on locally" and an expired password were reported at once (and all four were on Windows Server 2025). So the provider keeps a short trace (`Trace.h`):
- **What:** each provider instance's number, and the calls that decide the tile's fate – `SetUsageScenario`, `Advise`/`UnAdvise`, `SetUserArray` (the number of users), `GetCredentialCount` (and whether the ticket's account is among LogonUI's users), `GetCredentialAt`, the tile's `Advise`/`UnAdvise`, `SetSelected`/`SetDeselected` and `GetUserSid`, `GetSerialization`, `ReportResult` with its NTSTATUS and substatus and whether the result went to the service, and the pipe thread's steps (connected, claimed, ticket received, `CredentialsChanged` and its HRESULT, result sent, closed and why).
- **Never:** account names, SIDs, passwords or anything else from a ticket – only call names, codes, counts and instance numbers.
- **Where:** `%ProgramData%\RemoteHub\Agent\logs\provider-yyyyMMdd.log`, next to the agent's own log (SYSTEM and Administrators only, §8.4's folder rules; the agent's retention removes old files). The provider never creates the folder and doesn't write through a reparse point there.
- **Never blocks LogonUI:** a call only copies its line into a fixed queue (256 lines; more are counted and dropped). A thread-pool callback, which holds a reference to the DLL until it returns, appends them to the file.

## 9. After the sign-in – the unattended session (T6)

An administrator's desktop is now open on the physical console. Anyone standing at the device can see it and, without further measures, use the keyboard and mouse.

### 9.1 Privacy screen from the start

Privacy mode (0.18.3) provides what v2 had to leave out: an overlay on the device's screen that the admin's picture doesn't include, and a local input block. Automatic sign-in uses it as protection, not as an option (decided, §15 Q5):

- **On by default.** When the password has arrived and the sign-in begins (before the broker hands out the ticket), the service sets `_screen` to `DesktopPrivacyScreen("Message", BlockLocalInput: true)` and marks it as the service's (`_screenByService`), wherever the policy `PrivacyScreen` is 1 or 2.
  - The existing code then sends it to every helper that starts in the new session, after `Begin`. It's also kept with the resume token (privacy-mode-design §14). The sign-in screen itself isn't covered.
  - The `Message` style tells the person at the device that IT is working on it: heading "Remote Support Session in Progress", body "*DOMAIN\user* is signed in remotely. The screen stays hidden while they work.", and the line "Mouse and keyboard are blocked." It doesn't mention Disconnect (no banner since 0.18.10, §9.2).
  - **If the sign-in fails** (any `SignInFailed` after this point), the service's screen is dropped again, so a later sign-in by hand on the device's screen isn't covered.
  - **Audit:** the helper's report of the screen coming up is audited like the admin's own (1104 "*admin* … turned the privacy screen on …"), although the service turned it on. Product finding: the text should say the service did it (lab C-END-14).
- **No screen snips while it is up** (0.18.11, `SnipGuard`). The overlay is excluded from every capture, so a snip doesn't include it either. Windows then shows the snip's frozen picture full screen, above every topmost window. In manual testing, opening the Snipping Tool in the admin session made the screen visible at the device. While any privacy screen is up (not only after automatic sign-in):
  - Print Screen and Win+Shift+S are dropped by a low-level keyboard hook, for the admin's keys and the device's own.
  - A snipping tool that shows a window anyway (`ScreenClippingHost.exe`, `SnippingTool.exe`, `ScreenSketch.exe`), for example one started from the Start menu, is closed as soon as its window appears. This is logged in the agent's log.
  - Alt+Tab and Win+Tab are dropped as well, because their window thumbnails are drawn above topmost windows too.
- **The device's keyboard and mouse are always blocked** in a session created by automatic sign-in (0.18.11), whatever the `PrivacyScreen` level: at level 1, where the admin may not block local input themselves, it is still blocked for this session. Ctrl+Alt+Del can't be blocked – Windows handles it before any program – but while input is blocked the screen it opens offers only Task Manager and Cancel: Lock (Win+L too), Sign out, Switch user, Change a password and the power button are hidden (privacy-mode-design §5.9).
- **The `PrivacyScreen` right isn't needed to have it on.** The service turns it on for the session it created. It is needed to turn it **off**: an operator without the right can't remove it, and loses nothing by that. The overlay lets clicks through, and the admin's picture doesn't include it (privacy-mode-design §14).
  - **Resume keeps it.** A resumed connection restores the privacy screen when the operator holds the right, or when the resume entry marks it as the service's (`DesktopResume.Entry.ScreenByService`), regardless of the right.
- **When the device can't show it** (policy `PrivacyScreen` = 0, or Windows build < 19041, `DesktopEndpoint.PrivacyScreenSupported`) and `AutoSignInRequirePrivacyScreen` is 0, automatic sign-in still works, as in v2, without any cover and with only the end action (no banner either, §9.2). 1106 then says "no privacy screen on this device". In that case:
  - RemoteHub doesn't say so before the start yet (§16). The editor's setting and the install dialog say in general that anyone at the device can see the session while the operator works.
  - IT can turn automatic sign-in off for such devices with `AutoSignInRequirePrivacyScreen` = 1 (§5, §15 Q7).
- **Remaining gaps, from privacy mode itself:**
  - The secure desktop (UAC, Ctrl+Alt+Del screen) isn't covered until the lab test in privacy-mode-design §5.7 closes it.
  - Ctrl+Alt+Del can't be blocked locally. It opens the secure desktop with only Task Manager and Cancel (privacy-mode-design §5.9); Task Manager opens behind the overlay and can't be used with input blocked.
  - Between the sign-in and the new helper's `Begin` (start + about 1.5 s to let the session settle), the new desktop is on the screen without the overlay. The lab is to measure this gap (§14, not done yet). If it's longer than the Windows "Welcome"/first-sign-in animation, the helper sends the privacy screen before `Begin` for sessions created by automatic sign-in.

### 9.2 When the connection ends

- **No banner** (changed in 0.18.10). The session belongs to the admin, and nobody at the device owns it, so there is nothing for a person there to hide, show or end. The helper gets `HelperInit.SignedInRemotely` and shows only the privacy screen with its message. This applies to the helper started in the new session while the sign-in is being confirmed, and to a resumed connection. At the device, Ctrl+Alt+Del offers only Task Manager and Cancel (§9.1). With no privacy screen (policy `PrivacyScreen` = 0), nothing at the device shows that the admin is working.
- **When the connection ends on purpose,** the session created by automatic sign-in gets the **end action**: it is **locked** (`WTSDisconnectSession`) or **signed out** (`WTSLogoffSession`). Decided (§15 Q2):
  - **`Lock` is the default**, so the operator can come back to open work.
  - **Per connection** in RemoteHub: *When an automatic sign-in's connection ends*: *Lock the session* / *Sign out – unsaved work in the session is lost*, inherited like the other agent settings, sent in `DesktopStart` (§6.1).
  - **Device policy** `AutoSignInEndAction` (`Lock` | `SignOut`) **wins** when set, so IT can force `SignOut` on shared devices.
  - With `SignOut`, RemoteHub asks "Sign out on the device?" before disconnecting, because unsaved work in that session is lost.
  - "On purpose" means any of: closed by RemoteHub, idle timeout, a refusal or an invalid message from RemoteHub. A **dropped** connection is handled below.
  - **How to lock:** `WTSDisconnectSession` on the session, on client Windows and Server alike. On client Windows the console then shows the lock screen; on Server the console session is disconnected and the console goes back to the sign-in screen. Either way the session's programs keep running and the next sign-in of the account reconnects to it. The audit says "locked" in both cases. `LockWorkStation` from the helper isn't used.
  - **Every action checks the owner first:** the session must still belong to the account (by SID); otherwise nothing is done and only the log says so.
  - **Audit 1107:** "Automatic sign-in session *n* of *account* locked / signed out (*reason*)" ("couldn't be locked / signed out" when Windows refused). Reasons: "closed by RemoteHub", the idle reason ("no mouse or keyboard input for …"), "connection lost and not continued in time", "the screen cover ended after the connection dropped", "connection lost, nothing to cover the screen", "the agent restarted while it was open".
  - A drop the agent notices first (the keep-alive timeout or the aborted request cancels its read of the WebSocket rather than failing it) is a drop too, not "closed by RemoteHub": the session gets the 2-minute cover below, and a resume continues in it (lab C-END-06, C-END-15, D-RES-10).
- **A dropped connection: covered for 2 minutes, then signed out** (decided, §15 Q8). For a session created by automatic sign-in (the connection is "lost" in `DesktopEndpoint`'s terms, so a resume token was released):
  - **The helper keeps running** in the session for 2 minutes, with the privacy screen and the local input block as they were. It sends no frames and accepts no admin input; it only holds the overlay.
  - **No banner** (see above): the overlay stays as it is until the session is signed out or a resumed connection takes over.
  - **The resume window is 2 minutes** for this session instead of the usual 1 (`DesktopResume`), and RemoteHub keeps reconnecting for 120 s once it has seen `SignedIn`. A resumed connection of the same account (by SID) takes over the session's end action; it starts its own helper and ends the old one once the new one covers the screen, so the overlay doesn't drop in between, and work continues where it was.
  - **If nobody resumes within 2 minutes,** the session is **signed out** (`WTSLogoffSession`), **whatever the end action** says. The Lock/SignOut setting and the device policy `AutoSignInEndAction` apply only to connections that end on purpose. Audit 1107 "… signed out (connection lost and not continued in time)". The overlay stays until the session is really gone: Windows signs out asynchronously, so the helper is ended only once the session no longer belongs to the account (at most 60 s). If Windows refuses the sign-out, the session is locked instead and the helper ended then. If it refuses both, the helper keeps covering and the record stays for the next start (§9.3).
  - **If the helper itself ends during the 2 minutes** (crash, the console moving to another session), the overlay is gone. The service then signs the session out at once (1107 "… (the screen cover ended after the connection dropped)"), unless the session is locked at that moment: a locked session counts as safe and is left for the 2 minutes to run out. The person at the device can't lock it (Lock and Win+L are hidden while input is blocked, §9.1), so this applies only when Windows locks it itself, for example after the machine's inactivity limit.
  - **A drop while the helper isn't in that session** (for example while the picture is moving to the new session): nothing can cover it, so the session is signed out at once ("connection lost, nothing to cover the screen").
  - **An exception to privacy mode's rule** "everything ends with the helper" (privacy-mode-design §1.1 rule 5). The rule still holds, because the overlay ends with the helper. What's new is that the helper doesn't end with the connection for these sessions. Privacy-mode-design §14 gets a note.
  - **Why sign out rather than lock:** with the helper holding the overlay, a lock isn't needed during the 2 minutes. After them, nobody is coming back on this connection, and an unattended, auto-created admin session shouldn't stay on the device.

### 9.3 Crash safety

- Once the sign-in is confirmed (§6.3), the service records the session in `%ProgramData%\RemoteHub\Agent\autosignin.json` (`AutoSignInSessions`): session id, SID, account, end action, and whether its connection dropped (no secrets; ACL SYSTEM + Administrators, same as the rest of the folder). The entry is removed when the session gets its end action or is signed out, and marked as dropped while the 2 minutes of §9.2 run.
- **The entry goes only once the session is secured:** the end action done, or the session not the account's any more. If Windows refuses the lock or the sign-out (a refused sign-out falls back to a lock), the entry stays for the next start. A crash before the end action leaves it there too.
- **Saved in one step:** the file is written to `autosignin.json.tmp` and moved over the old one, one save at a time. A crash while saving leaves the previous complete file, never a truncated one.
- At service start (`AutoSignInSessions.Recover`), every entry left by the last run is dealt with, and the file is deleted once all of them are:
  - a session marked as dropped is **signed out** (§9.2), or locked if Windows refuses the sign-out;
  - any other session gets the end action (for example after the service was killed while connected).
  - Audit 1107 with reason "the agent restarted while it was open"; nothing is done when the session is gone or has another user now.
  - An entry whose action Windows refused again stays in the file for the next start.
- So if the service restarts during the 2 minutes, the session is signed out at start: the new service instance doesn't take over the old helper.
- Uninstall (`cleanup`) deletes the file.

## 10. Handling secrets in .NET (service and RemoteHub)

### 10.1 Service

- **A receive path of its own for `SignInRequest`.** Every other client message goes through `ReceiveWs` into a `MemoryStream` and `ToArray()`, copies that are never zeroed. `ClientLoop` is already running when `SignInReady` is sent, so the password would arrive that way. Instead:
  - `ReceiveWs` reads into a 64 KB receive buffer that is allocated once per connection and pinned (`GC.AllocateArray(pinned: true)`).
  - After the first fragment of a message, it looks at the type byte. For `SignInRequest` it reads the rest straight into a second pinned buffer (header + 256 characters), without the `MemoryStream`, and refuses anything larger (protocol error, the connection ends). A `SignInRequest` the agent didn't ask for, or a second one, is a protocol error as well.
  - It zeroes the receive buffer right after copying, and the second buffer once the password is protected.
  - Never through `DesktopWire.ReadJson`, `Encoding.GetString` or any `string`.
- **Protect it at once** with `CryptProtectMemory(SAME_PROCESS)`, padded to 16 bytes (`ProtectedSecret`, its own pinned buffer).
- **Unprotect it only to write it to the pipe,** into a pinned message buffer that is zeroed once written. `ProtectedSecret` is `IDisposable` and zeroes itself in `Dispose`; the connection disposes it when the sign-in ends, or at its end when the password was sent but never used.
- **What stays outside our control:** the TLS layer (`SslStream`) and Kestrel's buffer pools decrypt into buffers we can't zero. That's the same exposure as the Basic header on every connection (§6.1), so it adds nothing new. The doc says so rather than claiming more.
- **No dumps (not done yet, §16):** exclude the agent from Windows Error Reporting full dumps (`WerAddExcludedApplication`), or set `DontShowUI` + `LocalDumps\DumpType=0` for `RemoteHubAgent.exe` at install. Document this. The agent already sends no crash reports.
- **Logs:**
  - `AgentLog` never receives the message.
  - Add a test that greps the log and event log text for the test password after a full flow.

### 10.2 RemoteHub

- **Convert just in time:** stored passwords are sealed in the local database's `secrets` table and are a plain `string` in `CredentialSet.Password` once loaded – there is no `SecureString` path, and that string can't be zeroed. So: encode it into the outgoing binary frame only when sending (no other copy, never through JSON), zero the frame buffer after the send, and don't keep any further copy. A password typed in for this connection follows the same path.
- **Send conditions:**
  - only when the agent sent `SignInReady` in *this* connection;
  - the pinned fingerprint matched (through a jump host: the **target's** fingerprint, end to end, never the jump host's);
  - the operator asked for it at this start: the checkbox "Sign in automatically as *account*" in the agent desktop's start panel (preset from the connection's setting), shown only when Hello offers it, the chosen session is the console on the sign-in screen and the start isn't view-only (`AgentDesktopSession.CanAutoSignIn`).
  - Never on reconnect without a new `SignInReady`, and at most once per connection.
- **Missing or unusable password:** asking for it isn't built (§16). With no stored password RemoteHub sends nothing and says "Automatic sign-in needs a stored password for this connection – sign in on the device's screen instead."; a password over 256 characters gets "The password is too long for the automatic sign-in – sign in on the device's screen instead." The agent then gives up after 30 s (§6.2 check 8).
- **RemoteHub audit log** (`agent.auto_sign_in`, shown as "Automatic sign-in"): "Requested" when the password is sent, "Signed in automatically on the console" on `SignedIn`, "Failed – *message*" on `SignInFailed` after the password was sent. Never the password. The jump host isn't named in these entries yet (§16).
- **Policy:** `DisableAgentAutoSignIn` (DWORD 1) hides both editor rows (*Sign in automatically when nobody is signed in*, *When an automatic sign-in's connection ends*) and the start panel's checkbox; RemoteHub then never sends a `SignInRequest`.

## 11. Audit

**Agent (log file + Application log, source *RemoteHub Agent*).** 1104, 1105 and 2103 are now used by privacy mode (`DesktopPrivacyScreen`, `DesktopUserHid`, `DesktopPrivacyRefused`), so the IDs from v2 move:

| ID | Event | Level | Text |
|---|---|---|---|
| 1106 | `AutoSignInSucceeded` | Information | Automatic sign-in: *account* from *source* signed in to console session *n* (when the connection ends: lock / sign out, privacy screen on / no privacy screen on this device) |
| 1107 | `AutoSignInEnded` | Information | Automatic sign-in session *n* of *account* locked / signed out / couldn't be locked / couldn't be signed out (*reason*: "closed by RemoteHub", idle, "connection lost and not continued in time", "the screen cover ended after the connection dropped", "connection lost, nothing to cover the screen", "the agent restarted while it was open"). "Locked" on Server too (§9.2). |
| 2104 | `AutoSignInRefused` | Warning | Automatic sign-in refused: *account* from *source* – *message* (*code*): every refusal of §6.2 and the broker's failures (`notAllowed`, `unavailable`, `sessionState`, `existingSession`, `busy`, `rateLimit`, `timeout`, `noProvider`, `sessionChanged`) |
| 2105 | `AutoSignInFailed` | Warning | Automatic sign-in failed: *account* from *source* – Windows reported 0x*status* (0x*substatus*): *text* (§6.1) |
| 3101 | `AutoSignInMismatch` | Error | Automatic sign-in: the console session *n* has user *SID* (or no readable user), not *account* (*SID*) – signed out |
| 3102 | `AutoSignInUnavailable` | Error | Automatic sign-in unavailable: the pipe couldn't be created (squatted or otherwise). Registration and signature problems aren't events: they are the reason in the log and in 2104 (§8.4). |

A foreign pipe client is refused and only logged (§7.1). The service's own privacy screen shows up as 1104 under the admin's name (§9.1, product finding).

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

**Status (0.18.11, 2026-10-15):**
- **Phase 0:**
  - Done: `build/Sign-Files.ps1`; `build-installer.ps1 -SigningCertificate | -LabSigning` signs both exes and both MSIs, each before it's packed into the next step; `release.yml` signs through SSL.com eSigner (`-ESigner`, secrets `ES_*` and variable `SIGNING_CERTIFICATE_THUMBPRINT` in the environment `release-signing`) and builds unsigned, with a warning, until they exist; `lab/New-LabSigningCertificate.ps1`; `Install-LabAgent` makes each VM trust the lab certificate.
  - Open: the eSigner secrets in `release-signing`, then the first signed release.
- **Phase 1:**
  - Done: the provider; unit tests; the fuzzer (CI runs it for 60 s); the MSI component behind `AUTOSIGNIN`, which needs a signing certificate (`build-installer.ps1 -CredentialProvider`); the optional `autoSignIn` in `AgentInstaller`, with the install dialog's "Allow automatic sign-in (installs a credential provider)" and the right-click *Install / Remove credential provider* (`ChangeCredentialProvider`, audit `agent.credential_provider`); the Lab build and `RemoteHubCpTestServer.exe`.
  - Lab cases C-PROV-01 to 05 pass on Windows 11 26H2 and Server 2025: signed install, inert without a ticket, sign-in through the provider, kept on an update, removed with `AUTOSIGNIN=0`. C-PROV-06 to 09 (`lab/tests/C-PROV.Tests.ps1`) cover RemoteHub's install and remove on a running agent, another build refused, and the "isn't installed" / restart answers.
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
  - Lab: C-AUTO-01 to 06 (`lab/tests/C-AUTO.Tests.ps1`: turned off by IT, view-only, no password sent, wrong and right password, no password in the logs) and C-AUTO-07 to 13 (`lab/tests/C-AUTO2.Tests.ps1`: off without the install option, rights, privacy screen required, session state, rate limit, provider unusable, IT's own policy kept).
- **Phase 4:**
  - Done:
    - **Privacy screen from the start:** set when the sign-in begins (Message style; local input always blocked, at policy `PrivacyScreen` 1 too), so every helper in the new session gets it after `Begin`. The sign-in screen itself isn't covered. It's kept across a resume whatever the account's right (`DesktopResume.Entry.ScreenByService`), and dropped again if the sign-in fails.
    - **End action:** device policy `AutoSignInEndAction` (wins) → `DesktopStart.AutoSignInEndAction` (RemoteHub's setting *When an automatic sign-in's connection ends*) → Lock. Lock = `WTSDisconnectSession` (the lock screen on client Windows; a disconnected session on Server; audited as "locked" on both). `SignedIn` carries the end action to RemoteHub.
    - **Dropped connection:** `AutoSignInSessions.Dropped` keeps the helper, with its overlay and input block, for 2 minutes. A task reads and discards its output. The resume token lives as long, and RemoteHub keeps reconnecting that long. A resumed connection starts its own helper and ends the old one once the new one covers the screen. Without a resume, the session is signed out. If the helper ends sooner, it's signed out at once (a crash, a sign-out at the device), unless the session is locked at the device. No banner in these sessions since 0.18.10 (`HelperInit.SignedInRemotely`), only the cover.
    - **Crash safety:** `%ProgramData%\RemoteHub\Agent\autosignin.json` (session, SID, account, end action, dropped; no secrets). Left entries are dealt with at service start. Every action first checks the session still belongs to the account (SID). Deleted on uninstall.
    - **RemoteHub:** the setting, the end action shown in the `SignedIn` notice, and *Sign out on the device?* before a disconnect that signs out (tab X, middle click, Ctrl+W, toolbar Disconnect, closing the tab group).
    - Audit 1107 for each lock or sign-out.
  - Tests: unit/flow tests in `AgentDesktopFlowTests`. Lab C-END-01 to 08 (`lab/tests/C-END.Tests.ps1`): covered from the start, lock on close, sign-out from the connection or the policy, the policy winning, a network drop with and without resume, an agent restart, and the cover ending after a drop. C-END-09 to 15 are listed in `docs/test-lab-cases.md` and not run yet.
  - Not done: measuring the gap before the overlay appears (§9.1, lab).

### Agent (`src/RemoteHub.Agent`)
- [x] `AgentPolicy`: add `AgentRight.AutoSignIn` (default nobody), `NoUserBehavior` (`SignInScreen` default | `AutoSignIn`) and `AutoSignInEndAction` (`Lock` default | `SignOut`), plus `AutoSignInRequirePrivacyScreen` (DWORD, default 0). Include them in `Refused`. Add tests in `AgentDesktopTests`.
- [x] `ShellIdentity`: carry the user SID as `UserSid` (the resolved `DOMAIN\user` is already `Account`).
- [x] `Desktop/DesktopProtocol.cs`: `DesktopRights.AutoSignIn` (appended after `PrivacyInputBlock`), `DesktopFeatures.AutoSignIn`, `DesktopStart.AutoSignIn` and `DesktopStart.AutoSignInEndAction`, message `SignInRequest` (binary, password ≤ 256 characters), states `SignInReady` / `SigningIn` / `SignedIn` / `SignInFailed` + reason codes.
- [x] New `Desktop/AutoSignInBroker.cs`: the pipe and its instances (§7.1), peer checks (§7.2), ticket and the checks at `Claim` (§7.3), protected memory (§10.1), cancel on expiry, connection end and service stop.
- [x] New `Desktop/AutoSignInSessions.cs`: the end action, the 2 minutes after a drop and the sign-out, and crash safety (§9.2, §9.3).
- [x] `DesktopEndpoint` / helper: on a dropped connection of an auto-signed-in session, keep the helper with its overlay (no frames, no admin input); no banner (`HelperInit.SignedInRemotely`); a resumed connection starts its own helper and ends the kept one once it covers the screen.
- [x] `DesktopResume`: a 2-minute window for auto-signed-in sessions (the kept helper is held by `AutoSignInSessions`).
- [x] New `AutoSignInGate`: 3 attempts per SID per 15 min (§6.2 check 7).
- [x] `DesktopEndpoint`:
  - the checks in §6.2, including control and not view-only;
  - the flow in §6.4;
  - `_screen` set for the new session, and kept on resume without the `PrivacyScreen` right (§9.1);
  - `_allowedBy` from the SID check, and `IsSelf` by SID (§6.3);
  - the SID check after sign-in;
  - the `SignInRequest` receive path in `ReceiveWs` (§10.1).
- [x] `Program.cs`: create the broker before `app.RunAsync()`; run the end action for sessions left over in `autosignin.json`.
- [x] `AgentLog` / `AuditEvent`: IDs 1106, 1107, 2104, 2105, 3101, 3102.
- [ ] `Enrollment.cs`: `.rhagent` option `autoSignIn` (not done); `cleanup` removes the provider keys (not done – the MSI removes them) and `autosignin.json` (done). Done instead: `enroll … auto-sign-in=0|1` and `auto-sign-in on|off` (`AutoSignInSetup`, §5).
- [ ] `SelfTest`: report whether the provider is registered and signed, the pipe is up, and the privacy screen is available, without triggering a sign-in.

### Credential provider (`src/RemoteHub.CredentialProvider`, new)
- [x] Project, hardening flags, static CRT (§8.1).
- [x] `ICredentialProvider`, `ICredentialProviderCredential2`, `ICredentialProviderSetUserArray` (only to return the right user tile).
- [x] Console-session check, pipe client thread, server PID and image check, TLV parser (§7, §8.2).
- [x] `KERB_INTERACTIVE_UNLOCK_LOGON` packing; `ReportResult` → `Result` message.
- [x] libFuzzer target for the parser; unit tests for packing (the round trip against `LsaLogonUser` is still open).

### Installer and build
- [x] `installer/RemoteHub.Agent.Installer/Package.wxs`: property `AUTOSIGNIN` (Secure, next to `VIRTUALDISPLAY`); a component with the DLL and the two registry keys (CLSID `{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}`), conditioned on it; removed when uninstalling or when set to 0; **kept on an update without the property** (§8.4).
- [ ] `build/build-installer.ps1`, `ci.yml`, `release.yml`: build the vcxproj (`build/build-credential-provider.ps1`, `build-installer.ps1 -CredentialProvider`); **Authenticode signing of the DLL and the agent exe (blocker)** – the signing is built, the release certificate is missing (phase 0); the release build fails if the DLL isn't signed by the release certificate.
- [x] Agent install dialog (`AgentInstallDialog.cs`): checkbox "Allow automatic sign-in (installs a credential provider)", shown only when RemoteHub's package has the provider, preset to what RemoteHub last did on the device, silent installs only, with a short hint ("Anyone at the device can see the session while you work."); it doesn't name the case without the privacy screen separately.

### RemoteHub (`src/RemoteHub`)
- [x] `RdpOptions`: `AgentAutoSignIn` (bool?, inherited, default false) and `AgentAutoSignInEndAction` (`Lock` default | `SignOut`, inherited) + the editor UI under *PowerShell remoting* next to *Remote desktop through*. Run the ui-consistency process for the editor and tab changes.
- [x] Disconnect with end action `SignOut`: "Sign out on the device?" – unsaved work in the signed-in session is lost.
- [x] `AppPolicy`: `DisableAgentAutoSignIn`.
- [ ] `AgentDesktopSession` / `AgentDesktopView`:
  - the option in the start dialog, only when `Hello.Rights.AutoSignIn` and the start isn't view-only (done);
  - the notice when the device can't hide its screen (§9.1) (not done);
  - handle `SignInReady` → send the password once (§10.2) (done);
  - show the `SignInFailed` reasons (done: "Automatic sign-in didn't work: …");
  - show the privacy screen badge as already on after `SignedIn`.
- [ ] `AuditLog`: the new entries (done: `agent.auto_sign_in`, `agent.credential_provider`), with the jump host when there is one (not done).

### Docs
- [ ] `agent-spec.md` §12: move automatic sign-in from backlog to implemented; add the right and the policy values.
- [ ] `agent-desktop-draft.md` §4.4/§4.5/§6: point to this document and replace the `NoUserBehavior` row with the final values.
- [ ] `privacy-mode-design.md`: note that automatic sign-in turns the privacy screen on by itself (§9.1), and that its helper outlives a dropped connection for 2 minutes (§9.2).
- [ ] README *Remote desktop through the agent*: the setting, the four switches and the prerequisites, `AutoSignInRequirePrivacyScreen`, the privacy screen, the end action and the sign-out 2 minutes after a drop, the known limit (§9.1), the event IDs.

## 14. Tests

This is the full list for the finished feature. What exists in 0.18.11: the unit and flow tests in `tests/RemoteHub.Tests/AgentDesktopFlowTests.cs`, the provider's unit tests and fuzzer (`tests/RemoteHub.CredentialProvider.Tests`), and the lab scripts `lab/tests/C-PROV.Tests.ps1`, `C-AUTO.Tests.ps1`, `C-AUTO2.Tests.ps1` and `C-END.Tests.ps1` (cases and results in `docs/test-lab-cases.md` §9.12). Not built yet: the `ClrMD` heap test and the `LsaLogonUser` round trip (§16).

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
  - cancelled on disconnect; a session or policy change refused at `Claim`.
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
- Privacy screen: after `SignedIn`, the new helper gets `PrivacyScreen` with `BlockLocalInput` true (at policy level 1 too); after a failed sign-in it is gone; an operator without the `PrivacyScreen` right can't turn it off, and still has it after a resumed connection.
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
  - measure the time between the new desktop appearing on the device and the overlay (§9.1) (not written yet);
  - the console shows the cover's message ("*account* is signed in remotely. The screen stays hidden while they work."), there is no banner, and local keys and clicks have no effect (C-END-01; at `PrivacyScreen` = 1 too: C-END-09);
  - policy `PrivacyScreen` = 0 (`AutoSignInRequirePrivacyScreen` 0): sign-in works without a cover, 1106 says "no privacy screen on this device" (C-END-10); with `AutoSignInRequirePrivacyScreen` = 1 it is refused (C-AUTO-09);
  - a resume by an operator without the `PrivacyScreen` right brings the service's cover back (C-END-12); a failed sign-in drops it (C-END-13); the 1104 wording for the service's cover (C-END-14).
- Installer: update without `AUTOSIGNIN` keeps the provider; `AUTOSIGNIN=0` removes it and both registry keys (C-PROV-04, 05); RemoteHub's install and remove on a running agent (C-PROV-06 to 09); IT's own `NoUserBehavior` kept by install and uninstall (C-AUTO-13).
- Entra ID and hybrid joined devices (not proven yet):
  - Entra ID joined, cloud-only account (`AzureAD\user@tenant`): first check the agent's own sign-in, which uses a network logon.
  - Entra ID joined: the provider's password sign-in through Negotiate/CloudAP, the first sign-in on the device without internet, the passwordless policy, and Windows Hello for Business being required.
  - Hybrid joined with an on-premises account: expected to behave like a domain-joined device.
- Physical-console behaviour (`lab/tests/C-END.Tests.ps1`):
  - lock on close (C-END-02), sign-out from the connection or the policy (C-END-03), the policy winning (C-END-04);
  - a network drop: the overlay and the input block stay, resuming within 2 minutes continues in the same desktop (C-END-05), and without a resume the session is signed out 2 minutes after the drop (C-END-06);
  - no banner after a drop, only the cover; the cover's helper ending during the 2 minutes signs out at once (C-END-08);
  - local Ctrl+Alt+Del during the 2 minutes: only Task Manager and Cancel offered (Lock, Sign out, Switch user and Change a password hidden), and the session still signed out when the 2 minutes run out (C-END-11);
  - a drop the agent notices first (cut ≥ 75 s): handled like any drop – the cover for 2 minutes, then signed out (C-END-15);
  - killing the service (C-END-07), idle timeout, and a reboot during a session (the last two not written yet).
- Robustness: the provider registered while the service is stopped and while the pipe is flooded → the normal sign-in screen and Windows Hello PIN still work.
- `ExcludedCredentialProviders` with our CLSID, a registration pointing elsewhere, or a DLL not signed like the agent → the agent reports the feature off with the reason (C-AUTO-12).
- Already covered by the lab scripts besides the above: wrong and right password (C-AUTO-04, 05), no password in the logs (C-AUTO-06), view-only, missing password, rights, session state and rate limit (C-AUTO-02, 03, 07, 08, 10, 11).

## 15. Security review – decisions

The questions from v1 were decided by the owner on 2026-10-13. Q5 was reopened by privacy mode and decided again, and Q7–Q9 were added and decided, on 2026-10-14. The reviewer may still challenge any of them.

| # | Question | Decision |
|---|---|---|
| 1 | Is SYSTEM + LogonUI image + session id enough to identify the provider at the pipe, or use ALPC / local RPC with a security callback? | **Named pipe**, as in §7: less native code in LogonUI, easier to review and fuzz. |
| 2 | `Lock` or `SignOut` as the default end action? | **`Lock` by default**, configurable **per connection** in RemoteHub; the **device policy wins** when set (§9.2). |
| 3 | Limit the right to non-built-in accounts (refuse RID 500) or a dedicated group? | **No extra rule:** any account that holds the `AutoSignIn` right (default: the allowed groups; nobody until the switch to on by default) and authenticated the connection. |
| 4 | Re-send the password, or keep it protected from the Basic sign-in for the connection? | **Re-send on request** (§6.1), keeping the extra copy to seconds, and only for connections that use the feature. |
| 5 | Does T6 (a person at the console) need privacy mode before release? | v2: no, ship with a documented limit. **v3: the privacy screen is on.** Privacy mode now exists, so the session created by automatic sign-in starts with the privacy screen on, and local input is blocked when the policy allows it. Only an operator with the `PrivacyScreen` right can turn it off (§9.1). Where the device can't show it, the v2 behaviour and the documented limit stay. |
| 6 | (added) What does an agent update without `AUTOSIGNIN` do? | **Keeps what's installed**, like `VIRTUALDISPLAY` (§8.4). |
| 7 | (v3) Should IT be able to allow automatic sign-in only where the privacy screen works? | **Yes, by policy, default off:** `AutoSignInRequirePrivacyScreen` (DWORD, default 0). With 1, `DesktopRights.AutoSignIn` is false when the privacy screen isn't available (policy `PrivacyScreen` = 0 or build < 19041) (§5). |
| 8 | (v3) What happens to the session when the connection drops? | **The helper and its privacy screen stay up for 2 minutes, then the session is signed out.** A resume within 2 minutes continues in the same desktop. After a drop the session is always signed out, whatever the end action; Lock/SignOut applies only when the connection ends on purpose (§9.2). |
| 9 | (v3) Does automatic sign-in need the `Control` right and a non-view-only start? | **Yes** (§1.1, §6.2 check 2). A view-only operator couldn't type the password into the sign-in screen, so the baseline requires it. |

## 16. Not done yet (0.18.11)

- **Release signing certificate** (phase 0). Until it exists the provider ships only to lab VMs.
- **1104 for the service's privacy screen** names the admin as the one who turned it on (§9.1, product finding, C-END-14).
- **`AUTOSIGNIN=1` on a package without the provider** still writes the policy values (§5, product gap).
- RemoteHub: the notice before the start when the device can't hide its screen (§9.1); asking for the password when none is stored (§10.2); the jump host in the `agent.auto_sign_in` audit entries (§10.2).
- Agent: `SelfTest` reporting; `cleanup` deleting the provider keys itself; the `.rhagent` option `autoSignIn`; excluding the agent from WER full dumps (§10.1); `/analyze` in CI (§8.1).
- Tests: the `ClrMD` heap test and the `LsaLogonUser` packing round trip (§14); the robustness lab cases of §8.3 (service killed mid-claim, malformed and flooding pipe clients, 50 sign-ins in a row, PIN sign-in with the provider registered); measuring the gap before the overlay (§9.1); C-END-09 to 15; idle timeout and reboot during a session; domain, Entra ID, legal notice and jump host runs (§14).
- Phase 5: ADMX, README, `agent-spec.md`, the Defender/EDR lab run (§8.5) and the separate security review.
