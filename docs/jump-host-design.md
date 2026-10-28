# RemoteHub – Jump hosts

Status: brought in line with 0.18.11 (2026-10-15). Decisions in §12. **Phases 1–3 implemented** (§13–§15); the lab
tests run in `lab\tests\J-JUMP.Tests.ps1` (§11). What is still open: §16.
Owner: Grischa Ernst

## 1. Goal

RemoteHub can reach devices it has no direct network path to, by going through a device it can reach – a **jump
host** – that runs the RemoteHub Agent.

The route comes from the navigation: **a connection placed under another connection – directly or in folders below
it – is reached through it.** Moving it under a different connection changes the route; moving it back into a folder
outside any jump host makes it direct again. Nothing else needs to be set.

```
Customer A                 (folder)
  └─ JUMP01                reached directly, runs the RemoteHub Agent
       ├─ SQL01            through JUMP01
       ├─ WEB01            through JUMP01
       │    └─ (shares of WEB01, as today – also through JUMP01)
       └─ Branch office    (folder under the jump host)
            ├─ PC-0815     through JUMP01
            └─ PC-0816     through JUMP01
```

Everything a connection offers works through the jump host: RDP, the agent desktop, PowerShell (agent or WinRM), the
remote tools (files, registry, services, processes), file transfers and the connection's network shares. The target
needs no agent – only the jump host does.

### 1.1 Principles

1. **The route is the tree.** No separate route settings; what you see in the navigation is how RemoteHub connects.
2. **The jump host decides.** Whether, for whom and to where it relays is set by policy on the jump host only (§6).
   RemoteHub has no settings that widen or narrow it (client-side restrictions may come later, §12).
3. **End to end.** The jump host relays bytes; it never sees the protocols inside. RDP, WinRM over HTTPS, SFTP and the
   agent's TLS are encrypted from RemoteHub to the target, and the target's credentials never reach the jump host
   unencrypted (WinRM over HTTP: §9.2).
4. **Two sign-ins.** RemoteHub signs in to the jump host's agent with the jump host's credentials, and to the target
   with the target's credentials – exactly as if each were connected directly.
5. **Every relayed connection is audited on the jump host** (§7): who, from where, to which host and port, for how
   long, how much.

## 2. Non-goals (v1)

- **More than one hop.** The jump host itself must be reachable directly (§4.3). The design keeps a chain possible
  (§12).
- **Relay servers** (both sides dial out), **SSH jump hosts** and **RD Gateway**. Only the RemoteHub Agent is a jump
  host.
- **UDP.** RDP falls back to TCP through the tunnel.
- **FTP** (its data connections go to addresses the server announces).
- **Client-side restrictions** (which jump hosts or ports RemoteHub may use).

## 3. Threat model

| Threat | Answer |
|---|---|
| Someone uses the jump host to reach the internal network | Needs a Windows account with the right **Tunnel** on the jump host (§6.1), its pinned certificate, and the policy must allow the target and port (§6.2). Same brute-force limits as every agent sign-in. |
| The tunnel reaches services that only listen on the jump host itself (`127.0.0.1`, its own adapter addresses) or cloud metadata (`169.254.169.254`) | Refused unless the policy lists them explicitly: the jump host's own addresses only by an entry naming that single address, the other special addresses only by a range of /8 or narrower that contains them – never by a name (§6.2). |
| A host name passes the check, then resolves to another address (DNS rebinding) | The agent resolves once, checks each resolved address on its own, and connects only to an address it allowed (§5.3). |
| Another user or process on RemoteHub's PC uses the local forwarding port | The forwarder accepts connections from RemoteHub's own process only (§8.2). |
| The jump host is compromised | It sees who connects where and can drop or delay traffic, but not read it (principle 3). Impersonating the target fails on the pinned agent certificate, RDP's server certificate check (§9.1) and SSH host keys, as today. |
| An admin hides what they did by going through a jump host | Audited on the jump host (§7); the target audits its own sign-ins as before, with the jump host's address as the source. |

## 4. The model: connections under connections

### 4.1 Data

Today only folders contain items (`Folder.Children`, `NodeBase.Parent` is a `Folder`); a `Connection` holds its
`Shares`. New:

- `Connection.Children` – `ObservableCollection<NodeBase>`: connections and folders reached through it. Saved like
  `Shares`.
- **Container:** `Folder` and `Connection` share a small interface for "holds children" (`Children`, `IsExpanded`), and
  `NodeBase.Parent` becomes that container. Code that moves, removes or sorts items (drag and drop, cut and paste,
  delete, `RelinkParents`) works on the container, so it handles both.
- **Inheritance skips the jump host.** `Ancestors()` keeps returning folders only: for an item under JUMP01 it returns
  the folders between it and JUMP01, then JUMP01's own folders. So a child inherits credentials, RDP options, colour tag
  and "connect through" as if JUMP01 were a folder without settings – **never JUMP01's own credentials** (they are for
  the jump host).
- New `NodeBase.JumpHost` (`[JsonIgnore]`, computed): the first connection up the container chain, or null. Every
  item below JUMP01, however deep in folders, has `JumpHost = JUMP01`.
- **Saved next to the jump host** (implemented this way – older versions don't check a format version, so they would
  silently drop children stored inside a connection): in the shared file and in the database, the items directly under
  a jump host are saved on the jump host's level, right after it, each with `JumpHostId` = the jump host's id
  (`Services/TreeLayout.cs`: `Flatten` when saving, `Nest` when loading). An older RemoteHub shows them as ordinary items
  next to the jump host and loses only the route when it saves. Items in a folder below a jump host stay in that folder
  (the folder carries the id). An id whose jump host is missing, or a second hop, is ignored – the item stays where it is.
- Export and import, backups and history carry the children. Duplicating a jump host duplicates it without its
  children ("<name> (copy)", with its agent and its RDP certificate pin cleared, like every copy).

### 4.2 Navigation

- **Drag a connection or folder onto a connection** → it moves under it (dropping on the upper/lower edge still
  means "before/after", as today; the middle of the item means "into"). For keyboard users, the context-menu item
  **Move under** (a submenu, §13).
- **New folder** and **New connection** on a jump host's context menu create them under it; inside a folder under a
  jump host they work as in any folder.
- A jump host shows its children below its shares, a step in beside a guide line like a folder's contents (0.22.0;
  before, framed like a folder). Folders under it look like any folder.
- Each connection (and share on its own) below a jump host shows a route hint beside the address – **"via JUMP01"** – in
  the secondary text colour, in the warning colour when the jump host has no agent. Folders don't (their contents do).
  Its tooltip: "Reached through JUMP01 (its RemoteHub Agent is the jump host). Move it out of JUMP01 to connect
  directly."
- The header line of the agent desktop, PowerShell and tool tabs shows the route ("sql01.contoso.local via JUMP01"),
  a share tab's status "<description> · via JUMP01". The RDP tab doesn't show it.
- Search and filters find children; the tooltip's location includes the jump host ("Customer A › JUMP01 › SQL01").
- **Moving a jump host** moves everything below it with it. A move into or out of a jump host that changes the
  inherited credentials asks first ("Move – credentials change", as for folders).
- **Deleting a jump host** asks: *Keep them* (default – the items below it move next to it and are reached directly)
  or *Delete all*.

### 4.3 What can go where (one hop)

The rule: **nothing below a jump host may be a jump host itself.**

| Drop | Result |
|---|---|
| Connection or folder onto a connection that is directly reachable (no jump host above it) | Moves under it. |
| Anything onto a connection that is itself below a jump host | Refused – cursor "not allowed", tooltip "SQL01 is reached through JUMP01. Only one jump host is supported for now." |
| A jump host – or a folder containing one – onto a connection, or into a folder below a jump host | Refused – "JUMP02 is a jump host itself. …" / "JUMP02 in this folder is a jump host itself. …". |
| Connection or folder into a folder below a jump host | Moves there; it is now reached through that jump host. |
| Network share on its own onto a connection | Lands next to the connection (a drop never makes it a server's share); *Move under* puts it under a jump host (§15). A server's shares stay with their server. |
| `.rdp` file onto a jump host / a folder below it | Imported under it; onto a connection below a jump host: imported next to that connection (under the same jump host or folder). |

A connection may be placed under a jump host **without an agent** (to prepare the tree). Everything below it shows the
route hint in the warning colour with the tooltip "JUMP01 has no RemoteHub Agent – install it there to reach the items
below it.", and connecting says "SQL01 is reached through JUMP01, but JUMP01 has no RemoteHub Agent. Install it there
(right-click JUMP01 → Install RemoteHub Agent…), or move SQL01 out of JUMP01 to connect directly."

### 4.4 What a child offers

Everything a direct connection offers, through the jump host, except:

| Action | Behind a jump host |
|---|---|
| FTP shares | Can't be opened – *Open* is off in the menu with the reason ("Reached through JUMP01. FTP can't go through a jump host – its data connections go to addresses the server announces. SMB, SFTP and WebDAV can."); opened anyway, the same text. They stay saved. |
| SMB shares | Always through RemoteHub's built-in SMB client (§5.5); the tab says "SMB · RemoteHub client · via JUMP01". |

## 5. The tunnel

### 5.1 Endpoint

A new WebSocket endpoint on the agent: **`GET /v1/tunnel`**, HTTP Basic sign-in like `/v1/powershell` (same
`SignIn`, rate limits and lockout), right **Tunnel** (§6.1). A jump connection counts toward the agent's
`maxSessions` like any session (503 when reached). `/v1/health` lists the feature `tunnel` when the policy has
tunnelling on; RemoteHub doesn't check it first – it tries `/v1/tunnel` and explains the answer: 404 (turned off by
`TunnelEnabled`, or an agent older than jump hosts) and 403 (right `Tunnel` missing), §5.6.

### 5.2 One connection, many streams

RDP, WinRM, the agent and the file tools each open several TCP connections; signing in to the jump host for each one
would be slow and would run into the sign-in limits. So RemoteHub keeps **one** tunnel connection per jump host (and
set of credentials) and carries any number of streams in it – like SSH channels.

Binary WebSocket messages, each `[type: 1 byte][stream id: 4 bytes][payload]`:

| Type | Direction | Payload |
|---|---|---|
| `Open` | RemoteHub → agent | JSON `{ "host": "sql01.contoso.local", "port": 3389, "probe": false }` |
| `Opened` | agent → RemoteHub | JSON `{ "address": "10.20.1.15", "local": "10.20.1.2" }` – connected; `local` is the jump host's own address towards the target |
| `Refused` | agent → RemoteHub | JSON `{ "reason": "…", "message": "…", "local": "…" }` – reason `policy`, `unreachable`, `timeout`, `dns`, `limit` or `probe`; `local` as for `Opened` when the address was resolved and allowed (worked out without sending a packet, like `RemotingSetup.LocalAddressFor`) |
| `Data` | both | bytes (≤ 64 KB) |
| `Credit` | both | 4 bytes: more bytes the sender may send on this stream |
| `Close` | both | – (the sender closes its sending side; both closed → stream gone) |
| `Reset` | both | – ends the stream at once in both directions (§13) |

- **Probe:** an `Open` with `probe: true` is only resolved and checked, never connected: the agent answers `Refused`
  with reason `probe`, "sql01.contoso.local:58732 is allowed; not connected (probe).", and `local`. RemoteHub uses it
  for the firewall scope of an agent install (§8.4) – no TCP connection to the device.
- **Flow control per stream:** each side may have at most 256 KB unacknowledged per stream; `Credit` returns
  allowance as data is written onward. A large copy never stalls another stream (the RDP picture). A sender that
  overruns the window gets the stream reset.
- Stream ids are chosen by RemoteHub; the agent limits open streams per tunnel (`MaxTunnelStreams`, default 64).
- A text or garbled frame, an unknown type or an id that is already open is a protocol error: the agent ends the whole
  tunnel (audit 1202 "(protocol error: …)").
- Keep-alive: the WebSocket's ping every 30 s, as for the other agent connections. When the tunnel ends, every stream
  ends; RDP's auto-reconnect, the agent desktop's resume and the tools' reconnects open a new one.
- RemoteHub closes the tunnel after 2 minutes without streams.

### 5.3 What the agent does on `Open`

1. Reads the policy again (a changed policy applies to the next stream); too many open streams → `limit`.
2. Checks `TunnelEnabled` and the port (`TunnelPorts`). Refused → `Refused` `policy`, audited (2200).
3. Resolves the host name **on the jump host** (names behind the jump host often resolve only there), 10 s limit;
   no answer → `dns`.
4. Checks each resolved address on its own against `TunnelTargets` and the special addresses (§6.2). None allowed →
   `Refused` `policy`, audited (2200).
5. A probe ends here (`probe`, §5.2). Otherwise connects to the allowed addresses in turn (15 s limit in all) →
   `unreachable` / `timeout` (agent log only), or sends `Opened` and copies bytes until either side closes.
6. Audits the stream when it ends (§7).

### 5.4 How RemoteHub uses it

`JumpTunnels` owns the tunnel connections (§8.1). Two ways in, depending on who opens the socket:

- **In-process stream** – for code that can take a socket from RemoteHub:
  - The agent: every agent connection goes through `AgentClientTransport.Connect` (PowerShell, tools, desktop) or
    `AgentInstaller.HealthAsync`. Both get the socket from a `SocketsHttpHandler.ConnectCallback`
    (`ClientWebSocket.ConnectAsync(uri, HttpMessageInvoker, ct)`); TLS, the pin, the sign-in and the messages stay
    as they are. The URI keeps the target's name.
  - WebDAV (`HttpClient`, the same callback).
- **Local forwarder** – for components that only take a host and port: the **RDP control**, **WinRM**, **SFTP**
  (SSH.NET) and **SMB** (SMBLibrary). A listener on `127.0.0.1` with a free port, one per target and port, alive while a
  session uses it. Each accepted connection becomes one stream (§8.2).

The route comes from one place in the window – `TryRoute(connection)` (`MainWindow.JumpHosts.cs`) → null (direct) or a
`JumpRoute` (the jump host, its agent endpoint and its credentials, §8.1) – and is handed to each connect path as a
parameter: `AgentEndpoint.Via` (agent, health), `PsTarget.Via` (WinRM, agent install), `RdpRouteAsync` (RDP),
`ShareConnector.Open(…, via)` (shares), `RemotingSetupDialog`'s checks.

### 5.5 Per protocol

| What | Target port | Through the tunnel | Notes |
|---|---|---|---|
| Agent PowerShell, tools, desktop, health | agent port (58732) | in-process stream | TLS to the pinned certificate, end to end. |
| RDP | connection's port | local forwarder | Kerberos and the certificate name: §9.1. |
| WinRM (PowerShell, tools without agent) | 5985 / 5986 | local forwarder | §9.2. |
| SFTP | share's port | local forwarder | Host key checked as today. |
| SMB | 445 | **built-in client** only, local forwarder | Windows' own SMB client can't use another port before Windows 11 24H2 / Server 2025, and `127.0.0.1:445` is this PC's own server. Shares behind a jump host always use `ManagedSmbShare` (§15). |
| WebDAV | share's port | `ConnectCallback` | The HTTPS certificate is checked against the share's host name, as today – only the socket is tunnelled. |
| FTP | – | refused before connecting | Passive data connections go to addresses the server announces. |
| Availability checks (RDP, WinRM, agent) | each | a stream that opens and closes | `Refused` "unreachable" = port closed; refused by policy = "not allowed through JUMP01". |

### 5.6 Errors the user sees

Each names which step failed – jump host or target:

- Before connecting: "No credentials are set for the jump host JUMP01. …", or JUMP01 has no agent (§4.3).
- "Can't use the jump host JUMP01: …" – the connection to JUMP01's agent failed, with the agent's usual messages
  (unreachable, certificate, sign-in), and for `/v1/tunnel`:
  - 404: "The RemoteHub Agent on JUMP01:58732 can't be used as a jump host: jump connections are turned off by a policy
    on the device (TunnelEnabled), or the agent there is older than this RemoteHub – update it (…)."
  - 403: "The account may not use the RemoteHub Agent on JUMP01:58732 as a jump host: it must be in a group that the
    device's policy allows for jump connections (right Tunnel – by default the administrators)."
- Refused by policy: "The jump host JUMP01 doesn't allow this connection. " + the agent's message, e.g. "This jump
  host's policy doesn't allow connections to sql01.contoso.local (TunnelTargets)." / "… to port 3389 (TunnelPorts)." /
  "… is this jump host itself or a special address (…); its policy doesn't allow it (TunnelTargets)."
- Other refusals: "Through the jump host JUMP01: " + "The jump host can't resolve sql01.contoso.local." / "No answer
  from sql01.contoso.local:3389 in time." / "… can't be reached from the jump host (ConnectionRefused)." / "Too many
  connections through this jump host at once (MaxTunnelStreams = 64)."
- RDP, WinRM, SMB and SFTP only see their local connection closed; the forwarder keeps the jump host's answer and that
  is shown instead (§14).
- After that, the target's own errors, as today.

## 6. Policy (jump host only)

### 6.1 Right

New `AgentRight.Tunnel`, default: the groups in `agent.json` (Administrators). An account with the PowerShell right can
already open network connections from the device, so the default gives nothing new; IT can take it away separately
(subkey `Rights`, value `Tunnel`). Without it, `/v1/tunnel` answers 403 at sign-in, audited as a refused sign-in
(2000, "not a member of an allowed group"); PowerShell keeps working.

### 6.2 Values under `HKLM\SOFTWARE\Policies\RemoteHub\Agent`

| Value | Type | Default | Meaning |
|---|---|---|---|
| `TunnelEnabled` | DWORD | 1 | 0: this device is never a jump host (`/v1/tunnel` answers 404 before the sign-in, nothing audited; health doesn't list `tunnel`). |
| `TunnelTargets` | REG_MULTI_SZ | any | Allowed targets: `10.20.0.0/16`, `fd00::/8`, `10.1.2.3`, `sql01.contoso.local`, `*.contoso.local`, `*`. Each resolved address is checked on its own: it passes if it lies in an allowed range or the name matches a name pattern. The agent connects only to addresses that passed; a name with some allowed addresses isn't refused as a whole. |
| `TunnelPorts` | REG_MULTI_SZ | any | Allowed ports and ranges, e.g. `3389`, `5985-5986`, `58732`, `22`, `445`, `443`, `*`. Unset: every port, so connections on custom ports just work. |
| `MaxTunnelStreams` | DWORD | 64 | Open streams per tunnel connection (1–1024). |

Special addresses are never allowed by a name (which could resolve to anything), only by an address entry about them
(`TunnelRules.Allowed`):

- The jump host's own adapter addresses – only by an entry naming that single address (`10.20.1.2` or `10.20.1.2/32`);
  a range containing its subnet doesn't open them.
- Loopback (`127.0.0.0/8`, `::1`), unspecified (`0.0.0.0`, `::`), link-local (`169.254.0.0/16`, `fe80::/10`),
  multicast, broadcast and reserved (`240.0.0.0/4`) – only by a range of /8 or narrower that contains them
  (`127.0.0.0/8` does, `0.0.0.0/0` doesn't).

IPv4-mapped IPv6 addresses count as IPv4, and IPv6 scope ids are ignored when classifying.

A policy that can't be read refuses everything (as today: `AgentPolicy.Refused` turns tunnelling off – `/v1/tunnel`
answers 404). A `TunnelTargets` or `TunnelPorts` value that is set but has no readable entry (e.g. `not a host!`, `0`,
`99999`) allows nothing – every `Open` is refused with `policy`, never "any".

The README's policy list and `agent-spec.md` have the new values (there is no ADMX template).

## 7. Audit (on the jump host)

| Event | Id | When |
|---|---|---|
| `TunnelStarted` | 1200 | Tunnel connection signed in: account, source. |
| `TunnelStream` | 1201 | A stream ended: account, source, target name, address, port, duration, bytes each way. |
| `TunnelEnded` | 1202 | Tunnel connection closed: duration, number of streams, why ("closed by RemoteHub", "connection lost", "protocol error: …"). |
| `TunnelRefused` | 2200 | A stream refused by policy: account, source, target, port, which rule – "(TunnelTargets)", "(TunnelPorts)", "(own or special address)" or "(TunnelEnabled = 0)" when the policy changed while the tunnel was open. |

Refusals for `dns`, `limit`, `unreachable` and `timeout` are not audited; unreachable targets go to the agent's log
("Jump connection <account> from <source>: <host:port> unreachable (…)") – no security meaning, and noisy.
`TunnelEnabled=0` (404) and a missing right `Tunnel` (403, audit 2000) end before a tunnel exists (§6). Availability
checks are logged like any stream. RemoteHub's own history records sessions as today, without the route (§16).

## 8. RemoteHub side

### 8.1 `JumpTunnels`

- One tunnel per (jump host connection, credentials), opened on first use and shared by every session and tool tab of
  every child; closed after 2 minutes without streams.
- Signs in with the jump host's **effective** credentials (its own or inherited) and its pinned certificate –
  `AgentClientTransport.Connect` with the path `/v1/tunnel`. No credentials → "No credentials are set for the jump host
  JUMP01. …". Without a stored password RemoteHub asks once ("… it is kept in memory only.") and reuses it for every
  child and tab, or takes it from an open agent tab of JUMP01 with the same account.
- On a dropped tunnel every stream gets an error; the next connect reopens it. No silent retry of a stream that was
  already sending.

### 8.2 Local forwarder

- Listens on `127.0.0.1`, port chosen by Windows, never on other addresses.
- **Only RemoteHub's own process may connect.** On accept, the connecting port is looked up in the TCP table
  (`GetExtendedTcpTable`) and the owning process must be RemoteHub's – the RDP control and the WinRM client run in
  RemoteHub's process. Anything else is closed at once and logged. (On a terminal server, other users could otherwise
  use the tunnel with the admin's jump host sign-in.)
- One forwarder per (target, port); removed 1 minute after its last connection closes.

### 8.3 UI beyond the navigation

- The connection's (and folder's) editor shows a read-only text: "Reached through JUMP01 – its RemoteHub Agent is the
  jump host. Move it out of JUMP01 in the list to connect directly." (no link to JUMP01).
- The PowerShell target and tab header say "… via JUMP01".
- Connecting a child opens no tab for the jump host – the tunnel is invisible, only its errors show (§5.6).

### 8.4 Installing and updating the agent on a child

The target doesn't need an agent, but can have one (for the agent desktop and faster tools).

- **Install:** the installer copies the files and runs `msiexec` through PowerShell remoting (`agent-spec.md` §5.3).
  With WinRM through the tunnel (Phase 2) this works for children too. When WinRM isn't available, **Install RemoteHub
  Agent…** defaults to **Save the installer and the enrollment file to a folder…** – the MSI and the `.rhagent`
  enrollment file – for installing by hand or with software distribution. The dialog says "SQL01 is reached through
  JUMP01, so its agent will only be reached from there: allow connections from JUMP01's address in that network." and
  hides "Use the agent for this connection afterwards" (below a jump host an installed agent is used anyway).
- **Update** through the running agent works through the tunnel from Phase 1.
- **Firewall:** a child's agent is only ever reached from the jump host, so the button for the allowed remote addresses
  offers **the address the target will see** – "Add JUMP01 (10.20.1.2)", the jump host's own address in that network.
  RemoteHub gets it with a probe to the target's agent port (`JumpTunnels.LocalAddressAsync`, §5.2): the agent only
  resolves and checks, nothing is connected. If that fails (name not resolvable, refused by policy), no address is
  offered.

## 9. Technical risks

### 9.1 RDP: the target's name

The RDP control connects to `127.0.0.1:<port>`. Two things use the server name:

- **Kerberos (NLA/CredSSP)** asks for a ticket for `TERMSRV/127.0.0.1`, which doesn't exist → falls back to **NTLM**.
  Where NTLM is blocked (Protected Users, `RestrictNTLM` policies, newer Windows versions moving away from NTLM) the
  sign-in fails.
- **The server certificate** is issued to the target's name → a name-mismatch warning on every connection.

**First task of Phase 2 (spike, lab test J1):** find a supported way to give the control the real name while it
connects to the forwarder. Candidates, in order:
1. A property of the RDP control for the authentication target name (checked against the `IMsRdpExtendedSettings`
   and `IMsRdpClientNonScriptable` properties).
2. A per-target loopback name: a name that resolves to `127.0.0.x` only on this PC, without touching the hosts file
   (none known yet).
3. Accept NTLM, and replace the name check with a **pinned certificate**: the first connection shows the target's
   certificate thumbprint for confirmation; later connections compare it (like SSH host keys). The warning appears
   only when the certificate changes.

**Decided:** if only 3 works, RDP through a jump host ships with it (NTLM plus the pinned certificate), and
connections where NTLM is blocked are pointed to the agent desktop. Kerberos is not a requirement for Phase 2.

**Built:** option 3 (§14). RemoteHub checks the certificate itself and asks "Trust this RDP certificate?" once (pinned per
connection, "RDP certificate changed" when it differs); the RDP control then shows no warning of its own. Candidates 1
and 2 were not explored (§16).

### 9.2 WinRM through a local port

WinRM to `http://127.0.0.1:<port>` with explicit credentials → NTLM; WinRM's client refuses NTLM over HTTP to hosts not
in **TrustedHosts**. Options:

- Children with an agent use the agent for PowerShell (the default "connect through: automatic" already prefers it) –
  no WinRM at all.
- WinRM over **HTTPS** through the tunnel with `SkipCNCheck` (the name can't match `127.0.0.1`) – encrypted, and the
  certificate pinned the same way as RDP's (§9.1, option 3). **Recommended for children without an agent.**
- Plain HTTP: needs `127.0.0.1` in TrustedHosts – RemoteHub doesn't change that machine setting itself; the error
  says how. Over HTTP, WinRM's own message encryption still protects the content.

Built: HTTPS with `SkipCNCheck` (§14); lab test J-02 opens a session that way. Since 2026-10-28 the pin is actually
checked (until then only the chain was): before WinRM connects, RemoteHub reads the certificate through the jump host
(`PsRemoting.CheckCertificate`) – one this PC trusts for the target's name is fine, otherwise only the one trusted for
the connection (`WinRmCertificate`); a new or changed one is asked like RDP's. Whether `localhost` is treated
differently from `127.0.0.1` over HTTP was not tested (the lab host's TrustedHosts is left alone).

### 9.3 Throughput

Every byte crosses RemoteHub → jump host → target: one extra hop plus WebSocket framing (a few bytes per 64 KB). The
jump host's agent copies with pooled buffers; target: ≥ 50 MB/s per stream on a LAN (J8).

## 10. Phases

| Phase | Content |
|---|---|
| **1 – Tunnel, tree, agent targets** | Agent `/v1/tunnel` with streams, flow control, policy, audit (§5.1–5.3, §6, §7); `JumpTunnels` (§8.1); the tree (§4); agent PowerShell, tools and desktop to children (in-process stream); availability checks; errors (§5.6); install files and firewall pre-fill (§8.4). |
| **2 – RDP and WinRM** | Spikes J1/J2 first. Local forwarder with the process check (§8.2); RDP per the result of J1 (§9.1); WinRM per §9.2; agent install on children through WinRM. |
| **3 – Shares** | SFTP, SMB (built-in client), WebDAV. |
| Later | Multi-hop, client-side restrictions, FTP, relay servers. |

Unit tests: stream protocol (open, data, credit, close, refusals, limits) with an in-process agent like
`AgentDesktopFlowTests`; policy parsing and matching (ranges, patterns, ports, loopback/link-local, DNS answers with
mixed addresses); route resolution and the tree rules of §4.3; load and save of children, incl. an older file; the
forwarder refusing another process.

## 11. Lab tests

Built in the lab: `lab.json` `JumpHost` – the VM `RemoteHub-JumpHost` with an adapter on the Hyper-V private switch
`RemoteHub-Jump` (192.168.231.1/24). `lab\tests\J-JUMP.Tests.ps1` moves the VM under test behind it at run time (its
adapters onto that switch, a static address from .10 up); the revert undoes it and the jump VM is turned off afterwards.
Everything goes through RemoteHub's own client code with the route set (`AgentEndpoint.Via`, `PsTarget.Via`, harness
`--jump`). The lab's ids are J-00 … J-14 (`docs\test-lab-cases.md` §9.13, which also lists the UI cases J-15 … J-30).
The script runs J-00 … J-10, J-13 (dns / unreachable / timeout / limit refusals) and J-14 (`maxSessions`, unreadable
policy key); J-11 and J-12 below are partly covered by unit tests only. The PC running RemoteHub must not reach the
target directly – J-00 checks that.

| Id | Test |
|---|---|
| J0 | Direct connections from RemoteHub's VM to the target fail (RDP, WinRM, agent port). |
| J1 | RDP through the forwarder: the device's RDP certificate comes back through the tunnel (the spike's other candidates of §9.1 were not run). |
| J2 | WinRM through the forwarder: HTTPS to `127.0.0.1` with `SkipCNCheck` opens a session (HTTP needs TrustedHosts – not changed on the lab host). |
| J3 | Through the jump host: agent desktop (incl. consent, privacy mode, clipboard), PowerShell console, files, registry, services, processes. The jump host's audit shows each stream; the target's audit shows the jump host as the source. |
| J4 | RDP and WinRM (PowerShell and tools without an agent) through the jump host. |
| J5 | Policy: `TunnelEnabled=0`, `TunnelTargets` excluding the target, a port not in `TunnelPorts`, right `Tunnel` removed – each refused with the right message and audit entry. A name allowed by pattern resolving to a loopback address is refused. Loopback and `169.254.169.254` refused by default. |
| J6 | Tunnel drop (the jump VM's adapter towards RemoteHub off for 30 s): RDP auto-reconnects, the agent desktop resumes, tool tabs reconnect. |
| J7 | Another local process connecting to the forwarder's port is refused (`Test-NetConnection 127.0.0.1 -Port …`). |
| J8 | Throughput: a 2 GB file transfer to the target while its RDP session (or agent desktop) stays smooth. |
| J9 | Agent install on a child through WinRM, and by hand with the install files (firewall allows the jump host only); agent update through the jump host. |
| J10 | SFTP, SMB (RemoteHub's own client) and WebDAV shares of a child copy both ways; FTP is refused. |
| J11 | Move a connection and a folder between jump host, a folder under it, another jump host and a plain folder; the route follows for everything inside; the drops in §4.3 are refused; settings are inherited past the jump host (§4.1). |
| J12 | An older RemoteHub opening the new database and shared file (§4.1). |

## 12. Decisions and later work

| # | Question | Decision (2026-10-13) |
|---|---|---|
| D1 | Which jump hosts? | The RemoteHub Agent only – no SSH, RD Gateway or relay servers. |
| D2 | Which protocols? | Everything RemoteHub connects with: RDP, WinRM, agent, shares (FTP later). |
| D3 | Where is the route set? | The navigation: below a connection = through it (§4). |
| D4 | Folders under a jump host? | Yes, from the start (§4.1–4.3). |
| D5 | Multi-hop? | One hop for now. The protocol supports more unchanged (a tunnel inside a stream to the next jump host's agent); only the tree rule in §4.3 limits it. |
| D6 | Who decides what is allowed? | Policy on the jump host only (§6). Client-side restrictions later (allowed jump hosts, ports – as RemoteHub settings or IT policy on RemoteHub's PC). |
| D7 | On by default? | Yes, for Administrators (right `Tunnel`, `TunnelEnabled=1`) – PowerShell already allows the same; one value turns it off. |
| D8 | Default ports? | Any port (`TunnelPorts` unset). IT narrows it by policy. |
| D9 | RDP without Kerberos? | Acceptable: NTLM plus a pinned server certificate if spike J1 finds nothing better (§9.1). |

## 13. Implementation notes (Phase 1)

What was built, and where it differs from the sections above:

- **Agent:** `TunnelProtocol.cs` (shared wire contract), `TunnelEndpoint.cs` (one WebSocket, many relays; policy read
  for every `Open`), `TunnelRules.cs` (`TunnelTargets` / `TunnelPorts`), right `Tunnel`, audit 1200/1201/1202/2200,
  health feature `tunnel`. A value that is set but has no readable entry allows nothing.
- **A seventh message type, `Reset`:** ends a stream at once in both directions – a target connection that failed, a
  window overrun, or a target that didn't close within 60 s after RemoteHub closed its side. `Close` stays a half-close.
- **`TunnelTargets` matching (§6.2):** RemoteHub connects only to the resolved addresses that are allowed – by a range,
  by the name, or because no targets are set; a special address only by a range. A name with some allowed addresses is
  not refused as a whole, but the others are never used.
- **RemoteHub:** `Remoting/JumpTunnel.cs` (`JumpTunnels`, `JumpTunnel`, `TunnelStream`, `JumpRoute`,
  `JumpHostException`). `AgentEndpoint` carries the route (`Via`); `AgentClientTransport.Connect` and
  `AgentInstaller.HealthAsync` take their socket from the tunnel through `SocketsHttpHandler.ConnectCallback` – no local
  port in Phase 1.
- **Tree:** `NodeBase.Parent` is the container (folder or jump host); `Ancestors()` returns folders only (inheritance
  skips the jump host); `JumpHost`, `Descendants()` and `Contents` on every node; `Services/TreeLayout.cs` holds the
  rules (`WhyNot`) and the saved form (§4.1). Standalone network shares can't be placed below a jump host yet (Phase 3: they can, §15).
- **Navigation:** the middle of a connection takes a drop (top and bottom 30 % mean before/after); a refused drop says
  why in a tooltip at the pointer. **Move under** is a submenu (out of the current jump host; existing jump hosts and
  connections with an agent, at most 40) instead of a list dialog. *New connection / New folder here* on a jump host
  create the item under it. Deleting a jump host asks *Keep them, delete only this* (they move next to it) or *Delete all*.
- **Phase 1 limits shown to the user (lifted in Phases 2 and 3; only FTP stays off):** RDP, *Add network share* and the shares of a device below a jump host are in
  the menus but switched off, with the reason; opening them anyway explains it. "Connect through" and "Remote desktop
  through" are fixed to the agent below a jump host (the editor says so in their hints).
- **Install:** a new method for every connection, *Save the installer and the enrollment file to a folder*
  (`AgentInstallMethod.SaveFiles`); below a jump host it is the default (the clipboard stays available, for a remote session
  of the device opened elsewhere), and the firewall
  button offers the jump host's own address towards the device (from the agent's `local`).
- **Tests:** `JumpHostTests` (agents on 127.0.0.1: PowerShell and health through a jump host, data both ways with
  several streams, audit, policy refusals incl. DNS answers that point at special addresses, unreachable ports, a wrong
  jump host password, the stream limit, idle close) and `JumpHostTreeTests` (inheritance, saved form for older
  versions, database round trip, the one-hop rules, copies).
- **After the code review:** the jump host's own adapter addresses count as special (only an entry naming that exact
  address allows them); other special addresses need a range of /8 or narrower (`0.0.0.0/0` doesn't open the loopback);
  IPv6 scope ids are ignored when classifying. A reset (or a lost tunnel) is an `IOException` for the reader, never a
  normal end of stream. An idle tunnel closes with a proper WebSocket close (audited as "closed by RemoteHub").

## 14. Implementation notes (Phase 2)

- **Local forwarder:** `Remoting/JumpForwarder.cs` (`JumpForwarders`, `JumpForwarder`, `LocalForward`). One listener on
  `127.0.0.1` per route, target and port, held by a lease (an RDP tab, an open WinRM runspace) and by its open
  connections; it closes 1 minute after the last one ends. Each accepted connection becomes one tunnel stream, both
  directions copied with half-close (`TunnelStream.CloseOutput` sends `Close` and keeps reading).
- **Process check (§8.2):** the accepted connection's row in `GetExtendedTcpTable` (IPv4, owner PID) must belong to
  RemoteHub's own process; anything else is closed at once and written to the diagnostic log.
- **Errors:** RDP and WinRM only see "connection closed" when the jump host refuses a stream; the forwarder keeps the
  jump host's answer, and the RDP tab and the PowerShell error show it instead.
- **RDP (§9.1):** the session goes with option 3 (lab J-01 checks the certificate through the tunnel). Before each connect RemoteHub opens a stream to
  the RDP port itself, does the X.224 security negotiation and the TLS handshake, and reads the certificate
  (`ServerCertificates.RdpAsync`). A certificate this PC trusts for the target's name is accepted as a direct connection
  would; otherwise the user confirms it once (subject, issuer, expiry, thumbprint, and the PowerShell line that shows the
  thumbprint on the target), and it is saved with the connection (`Connection.RdpCertificate`, SHA-256). A different one
  asks again ("RDP certificate changed"). Checked, the control connects to `127.0.0.1:<port>` with
  `AuthenticationLevel = 0` (it would only see that 127.0.0.1 isn't the certificate's name). Servers with the old RDP
  security have no certificate: the control's own setting applies. The check and the control's own connection are two
  connections; with NLA the sign-in itself (CredSSP) is bound to the server's key, so a different server in between
  can't complete it. Sign-in falls back from Kerberos to NTLM (D9).
- **WinRM (§9.2):** WinRM goes to the forwarder's port with `SkipCNCheck` over HTTPS – the
  certificate must be trusted on this PC for the target's name, or be the one the user trusted for the connection –
  checked through the jump host before every sign-in, as for RDP (§9.2) – and NTLM over HTTPS is bound to it (channel binding). Over HTTP
  the WinRM client refuses NTLM to 127.0.0.1 unless it is in TrustedHosts; the error says so and points to HTTPS or the
  agent. The setup script's firewall scope is the jump host's address in that network.
- **Which way below a jump host:** the Phase 1 lock to the agent is gone. "Connect through" applies as everywhere, except
  that an installed agent is used unless the connection itself is set to PowerShell remoting – an inherited setting
  was chosen for devices reached directly (`Inheritance.UsesAgent`). "Remote desktop through: automatic" picks the agent
  when installed, otherwise RDP.
- **Install over WinRM through a jump host** works like a direct one (`PsTarget.Via`); *Save files* stays the default
  when WinRM isn't available.
- **Tests:** forwarder through a real jump host agent (data and half-close, one port per target), another process
  refused (`powershell.exe` connecting to the port), a refused target as the forwarder's error, idle close, the RDP
  negotiation and certificate (TLS and old RDP security).
- **Lab:** J-01, J-02, J-04, J-06, J-07 and J-09 run in `J-JUMP.Tests.ps1` (§11).

## 15. Implementation notes (Phase 3)

- **One entry:** `ShareConnector.Open(…, via)` – the route comes from `TryRoute` on the share's server (or, for a share on its
  own, the stand-in connection whose parent is the share's), so passwords for the jump host are asked and remembered as for
  the other tools. A reconnect asks for the route again (the share may have moved).
- **SMB:** always `ManagedSmbShare` (SMBLibrary) through a local forwarder. SMBLibrary 1.5.8.1 (the latest) only offers port
  445 publicly; RemoteHub calls its private `Connect(IPAddress, SMBTransportType, int port)` – the one the public overloads
  call – after setting `m_serverName` to the share's host, so the NTLM SPN (`cifs/<host>`) and the tree-connect path
  (`\\<host>\<share>`) carry the real name although the socket goes to `127.0.0.1`. A test fails if an update removes
  either member (`ManagedSmbShare.CanUseOtherPort`).
- **SFTP:** SSH.NET only takes a host and port → local forwarder; the host key is checked against the share's fingerprint,
  and the prompt names the share's host.
- **WebDAV:** in-process – `SocketsHttpHandler` with the agent's `ConnectCallback` (`AgentClientTransport.ThroughJumpHost`);
  the URL, the certificate check and Kerberos/NTLM keep the share's name. Direct WebDAV uses `SocketsHttpHandler` too
  (what `HttpClientHandler` wraps).
- **FTP:** refused before connecting (`ShareConnector.FtpNotThroughJumpHost`); its menu entries are off with that reason.
- **Errors:** SMB and SFTP only see the local connection closed; the forwarder's kept answer from the jump host becomes the
  error, as for RDP and WinRM.
- **Tree (§4.3 change):** a share on its own may be placed directly under a jump host or in a folder below one (saved next
  to the jump host with `JumpHostId`, like connections; an older RemoteHub shows it as a direct share). It still can't go
  under a connection that is itself below a jump host, and a server's share stays with its server. Standalone shares have
  *Move under* in their context menu, and show *via JUMP01* beside their address (`RouteHint` moved to `NodeBase`).
- **Tests:** WebDAV through a real jump host agent with a name that only the jump host resolves (the Host header keeps it);
  SMB, SFTP and WebDAV refused by the jump host's policy give its message; FTP isn't tried; SMBLibrary's port overload;
  standalone shares below a jump host in the tree rules and the saved file.
- **Lab:** J-10 (SMB, SFTP and WebDAV against real servers behind a jump host; FTP refused) runs in `J-JUMP.Tests.ps1`.
- **Share editor:** for a jump host's own share on another host it warns "Reached directly, like JUMP01 itself. For a
  device reached through JUMP01, add the share to that device's connection, or place a share on its own below
  JUMP01."; for a child's share on another host "Another device, reached through JUMP01 like SQL01." *Add network
  share…* on a child is on; its tooltip says FTP can't go through a jump host.

## 16. Not done yet

- **More than one hop** (D5): only the tree rule limits it – "… Only one jump host is supported for now."
- **Client-side restrictions** (D6), **relay servers**, **SSH jump hosts**, **RD Gateway**.
- **FTP through a jump host.**
- **Kerberos for RDP through a jump host** (§9.1 candidates 1 and 2); WinRM over HTTP to `localhost` (§9.2).
- **The route in RemoteHub's session history** (§7) and on the RDP tab (§4.2).
- **Cut and paste in the navigation** (moving is drag and drop or *Move under*).
- **An ADMX template** for the agent policy (the values are in the README).
- **Lab:** the UI cases J-11, J-12 (partly) and J-15 … J-30 of `test-lab-cases.md` §9.13.
