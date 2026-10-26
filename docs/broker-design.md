# RemoteHub Broker

Status: Draft v3 (2026-10-26) – decisions in §18 (v3: shared devices, organisation secrets, install and assign).
Nothing built yet. Phases and tests in §17.
Owner: Grischa Ernst

## 1. Goal

RemoteHub reaches a device **from anywhere** – home office, hotel, another company's network – without a VPN, port
forwarding or an inbound firewall rule on either side. In the same network nothing changes: RemoteHub connects
directly, as today.

A new component, the **RemoteHub Broker**, is the one fixed point both sides know:

1. Every agent keeps one outbound connection to the broker.
2. RemoteHub asks the broker for a device; the broker introduces the two sides.
3. They try to reach each other **directly** (same LAN, or across both NATs by hole punching). The broker is then out
   of the path.
4. Where no direct path can be made, the broker **relays** the traffic.

Devices **register themselves** with the broker (an MSI property, or assigned later), and **every console of the
organisation sees the same devices** with their online state in a synced folder (§11.4) – no shared settings file
needed. Credentials stay on each console.

```
                     ┌──────────────────────────────┐
                     │ RemoteHub Broker (Cloudflare)│
                     │  Workers · Durable Objects   │
                     └──▲──────────────────────▲────┘
       outbound TCP 443 │                      │ outbound TCP 443
┌───────────────────────┴───┐            ┌─────┴───────────────────────┐
│ RemoteHub (admin PC)      │            │ RemoteHub Agent (device)    │
│ home / hotel / customer   │◀══════════▶│ behind the company's NAT    │
└───────────────────────────┘  direct    └─────────────────────────────┘
                               (QUIC over UDP, hole-punched) – or relayed through the broker
```

Later the broker also becomes the **management server** (sign-in, RBAC, inventory, central audit, a hosted offer –
§16). This document designs the connection broker only, but keeps identities, data and endpoints so the management
parts are additions.

### 1.1 Principles

1. **Outbound only.** Agents and consoles only dial out. Nothing listens for the internet on a device or an admin PC
   (§3).
2. **The broker introduces, it doesn't trust.** The device's pinned certificate and the Windows sign-in on the device
   stay end to end, exactly as on a LAN. A compromised broker can refuse, delay and see *who connects to which device*
   – it can't read the traffic, sign in to a device or impersonate one (§4).
3. **Same network, no broker.** Each connection chooses how it is reached – automatically, directly or through the
   broker – for the agent and for RDP alike (§11.1). On the LAN the broker isn't involved at all.
4. **Direct first, relay always works.** A session never fails because hole punching failed; it only gets slower.
5. **Nothing above the transport changes.** The broker delivers a byte stream to the agent's existing HTTPS endpoint.
   PowerShell, tools, the agent desktop, updates and the jump host tunnel run on it unchanged – and with them RDP to
   the device and to devices behind it (§9).
6. **The device decides.** Whether a device uses the broker, may be reached directly and reveals its local addresses
   is the device's policy (§13.1).
7. **Cloudflare-native, self-hosted first.** v1 is deployed by each organisation into its own Cloudflare account.
   A broker hosted for customers comes later (§16); the data carries a tenant id from the start so that needs no
   migration of the model.

## 2. Non-goals (v1)

- **User sign-in, RBAC, inventory, policy distribution** – the management server (§16). In v1 a console is identified
  by a key it registered with the organisation's console secret (§6.3), and every console sees every device (§18 D9).
- **Sharing credentials.** The broker never holds a password; each console keeps its own (§11.4).
- **RDP, WinRM or shares to a device without an agent** except through an agent acting as jump host (§9).
- **The hosted, multi-tenant broker** and **AWS** (§16).
- **Migrating a running stream** from relay to direct (§8.6).

## 3. Network requirements – what IT has to allow

| Who | Needs | For |
|---|---|---|
| Agents and consoles | **outbound TCP 443** to the broker's name | everything: registration, signalling **and the relay** – this alone is enough for every feature |
| Agents and consoles | outbound **UDP 3478** to the STUN service (default `stun.cloudflare.com`) | learning their public address (§8.2) – direct path only |
| Agents and consoles | outbound **UDP to any address and port**, and its replies | the direct path across the internet (§8.3) – the other side's public address isn't known in advance |
| Anyone | **nothing inbound** | – |

So **one name whitelisted outbound on TCP 443 is enough**: the console never needs to be reachable, and neither does
the device. In a network that allows only the broker, every session runs **relayed**, automatically and without an
error. The direct path is a speed-up:

- **Same network:** with *Reach: Automatic* RemoteHub connects directly to the device's address, as today – the broker
  isn't used (§11.1). And a broker session between two PCs of one site still finds the local path (host candidates,
  §8.2).
- **Across the internet:** direct needs outbound UDP on *both* sides and NATs that keep the same public port for
  different destinations (most home routers and many firewalls; not symmetric NAT, carrier-grade NAT on both sides or
  "UDP blocked"). Typical: home office to a company with open outbound UDP → direct; hotel Wi-Fi or strict corporate
  egress → relayed.
- **By name, not address:** Cloudflare serves the broker from anycast addresses that are shared and change, so rules
  match the name (TLS SNI or the proxy's `CONNECT` host). Rules that need addresses use Cloudflare's published ranges.
- **Proxies and TLS inspection:** the TCP connections go through an HTTP proxy (`CONNECT`); the agent uses the
  machine's WinHTTP proxy or `BrokerProxy` (§13.1). Because agents and consoles prove their keys *inside* the
  connection (§6) and every stream to a device is TLS to its pinned certificate, an inspecting proxy that the machine
  trusts doesn't break anything – it sees the broker's signalling, never the device traffic.

## 4. Threat model

| Threat | Answer |
|---|---|
| Someone on the internet attacks the agent | With `ListenEnabled = 0` (§10.3) the device has no open port. A hole-punched UDP port only accepts QUIC from the one console key the broker announced for that session (§8.4); behind that are the unchanged TLS pin and Windows sign-in. |
| A compromised or fake broker sends a console to the wrong device | The console checks the device certificate pin on every stream before sending credentials, as today (agent-spec §4.1). The fake device never sees a password. |
| A compromised broker reads or changes traffic | Every stream is TLS to the pinned device certificate, end to end through the relay (principle 2). The broker sees sizes, times and both public addresses. |
| A compromised broker opens sessions to every device | It gets what an attacker on the device's LAN gets today: the agent's TLS endpoint, which still needs a Windows account with the right on the device. The agent audits every broker session (§14.1). Devices can refuse the broker (`BrokerEnabled = 0`). |
| The Cloudflare account is taken over | Same as a compromised broker. The account needs MFA and few administrators (deployment guide, §12.5). Cloudflare as operator sees the same metadata. |
| A stranger uses the broker (connects to devices, uses the relay) | Only registered consoles may ask for a session; registering needs the organisation's console secret (§6.3). Relay connections need the session's token (§8.5). |
| A compromised broker swaps a device's certificate, name or folder | Device records are sealed with the organisation's record key, which the broker never has (§6.1). A console refuses a record without a valid seal ("not sealed by your organisation") and never connects to it. |
| The **device secret** leaks (it is on every device, in Intune, in install scripts) | It only lets someone register **new** devices. They land in *New devices* without inherited credentials, marked new, with a warning on a duplicate name (§11.4). Existing devices can't be taken over: the broker binds an id to its key at registration, and a console notices a changed pin for a device it knows (only together with a compromised broker could a leaked device secret replace one). Rotating the secret stops new registrations with it (§6.5). |
| The **console secret** leaks | It lets someone register a console: see the device list and ask for sessions – still stopped by the Windows sign-in on every device. Kept on admin PCs only; rotate it and remove unknown consoles (§6.5). |
| A rogue device with a known name ("DC01") collects passwords | New devices land in *New devices*, where no folder credentials are inherited; a duplicate name is flagged; a console sends a device's credentials only after its pin was seen in a sealed record (§11.4). |
| The broker – or a device with a leaked device secret – moves a device into a folder with powerful credentials | Placement is sealed with the layout key, which only consoles have (§6.1); an unsealed move is ignored and the device shown where its last valid seal puts it. |
| The broker makes agents send UDP to arbitrary addresses (scanning, reflection) | Punch packets go only to candidates of a session the agent accepted: at most 8 addresses, 200 packets of 44 bytes in 3 s, never multicast, broadcast or unspecified addresses (§8.3); sessions per console are limited (§12.4). Not a useful reflector. |
| The device's internal addresses leak to consoles | Host candidates reveal local addresses to the console (through the broker). `BrokerDirect = 1` sends only public candidates, `0` none (§13.1). |
| Brute force against devices through the broker | The agent's sign-in gate keys on the console's public address as the broker saw it (§10.2); the broker limits session requests per console (§12.4). |
| Denial of service against the broker | It stops reaching devices *from outside*; on the LAN, direct connections keep working (§11.1). Cloudflare absorbs volumetric attacks; per-console and per-agent limits (§12.4). |
| RDP to the device itself through the tunnel opens more than RDP | The exception is the device's own RDP port only, read on the device – the caller can't choose another port or address (§9.1). |

## 5. Architecture

```
RemoteHub (console)               RemoteHub Broker (Cloudflare)              Device
┌─────────────────────┐  wss 443  ┌──────────────────────────────┐ wss 443  ┌───────────────────────────────┐
│ BrokerClient        │─control──▶│ Worker: routing, auth        │◀─control─│ BrokerLink (agent service)    │
│  ├ registration     │           │ DeviceHub DO (one per device)│          │  ├ registration, keep-alive   │
│  ├ device list      │           │ TenantHub DO (list, presence)│          │  ├ self-registration          │
│  └ PeerSessions ────┼─relay────▶│ RelaySession DO (per session)│◀─relay───┤  ├ accept / refuse sessions   │
│     ├ RelayChannel  │           │ D1: tenants, devices,        │          │  └ PeerListener ──┐           │
│     └ QuicChannel ◀═╪═══════════╪═ consoles, keys, audit ══════╪═════════▶│                   ▼           │
│        │ streams    │  direct (UDP, QUIC) – not via Cloudflare │          │  Kestrel (unchanged endpoints)│
│        ▼            │           └──────────────────────────────┘          │  /v1/powershell /v1/desktop   │
│ AgentClientTransport│                        STUN: stun.cloudflare.com     │  /v1/tunnel /v1/health        │
│ JumpTunnels, ...    │                                                      └───────────────────────────────┘
└─────────────────────┘
```

- **Control channel:** one WebSocket from each side to the broker (§7). JSON, one message per WebSocket message,
  ≤ 64 KB.
- **Peer session:** between one console and one device, carrying any number of **streams**. Each stream is a byte
  stream to the agent's own HTTPS endpoint – the same bytes as a TCP connection to port 58732 today.
- **Two channel kinds behind one interface** (`IPeerChannel.OpenStreamAsync`): QUIC (direct, one QUIC stream per
  stream) and relay (one WebSocket through the broker, multiplexed with the jump host's framing, §8.5).

### 5.1 The rule that keeps the agent's boundary

**A peer session reaches only the agent's own HTTPS endpoint.** A stream has no target – it always ends at the
agent's Kestrel, before any sign-in. Everything else – RDP to the device, other hosts – is the jump host function
(`/v1/tunnel`): signed in, right `Tunnel`, `TunnelRules`. So the broker adds no way past the agent's sign-in, rights
or tunnel policy.

## 6. Identities and the organisation's secrets

The broker runs behind Cloudflare's edge, which terminates TLS, so identities are proven **inside** the WebSocket, not
by TLS client certificates: after the upgrade the broker sends `Challenge {nonce}`, the client answers
`Proof {id, signature}` over `"remotehub-broker/1" | role | id | nonce | broker host`. The broker checks it against the
registered public key (WebCrypto: ECDSA P-256, RSA-PSS). Nothing is accepted before a valid proof; a wrong one closes
the connection.

Until the organisation's PKI takes over (§16), **two shared secrets** say who belongs to the organisation: one on
every device, one on the admin PCs. They are built so that **the broker never holds what it would need to forge a
device** – otherwise a compromised broker could hand consoles a fake device and collect passwords (principle 2).

### 6.1 Two secrets, three keys

`rhbroker tenant init` creates both secrets **on the administrator's PC** (256 bits each, with an id):

- **Device secret** `rhd1-<id>-<base32>` – goes into the agent MSI or `broker join` (§6.4); ends up on every device.
- **Console secret** `rhc1-<id>-<base32>` – for admin PCs only. It contains the device secret too, because consoles
  need its record key.

Every side derives the keys it needs (HKDF-SHA256, one label each):

| Key | Derived from | Held by | Used for |
|---|---|---|---|
| **Enroll key** | device secret | devices, consoles, **broker** | a registering device proves it belongs to the organisation |
| **Record key** | device secret | devices, consoles – **never the broker** | **seals** device records |
| **Join key** | console secret | consoles, **broker** | a registering console proves it belongs to the organisation |
| **Layout key** | console secret | consoles only – **neither the broker nor devices** | **seals** labels, folders and placements |

The CLI uploads only the enroll and join keys; the secrets themselves never reach the broker.

**Seals** are `HMAC-SHA256(key, canonical JSON)` over what decides where credentials go:

- **The device record** (record key) – device id, certificate fingerprint, computer name, domain, registration time.
  Made by the agent when it registers, or by the console that created its enrollment.
- **The layout** (layout key) – labels, the folder tree (id, parent, name) and each device's placement in it. Made by
  the console that changed them. Devices don't have this key, so someone holding a leaked device secret can register
  a device but never place it next to the servers.
- Each with a version that only goes up: a console keeps the highest version it has seen, so the broker can't replay
  an old seal to undo a move.

A console uses only sealed data. Agent version, OS, online state and last seen are information only, not sealed.

### 6.2 The broker's own name

- A Cloudflare custom domain such as `rh.contoso.com` with Cloudflare's certificate, checked normally by chain and
  name. No pin – Cloudflare rotates its edge certificates.
- This is enough because the broker isn't trusted with anything secret (principle 2, §6.1).

### 6.3 Consoles

1. In RemoteHub, *Settings → RemoteHub Broker → Connect*: the broker's address and the console secret – pasted, or
   from a `.rhbroker` file the administrator hands out.
2. RemoteHub creates an **ECDSA P-256 key** (non-exportable, CNG, current user) and registers it with a proof made with
   the join key, under a name (computer and user, editable: "Alice – laptop").
3. From then on it proves that key on every connection (§6). The console secret stays in the local database,
   encrypted for the current user (DPAPI), because the record key is needed to check and make seals.

- **Per PC and user**, never in the shared settings file.
- *Settings → RemoteHub Broker* lists the consoles; any console can remove another in v1 (they are all equal until
  RBAC, §18 D9), and so can `rhbroker console remove <id>`. A removed console's key is refused from then on.
- **Long term: the organisation's PKI** (§16) replaces the console secret – the proof then carries a certificate from
  the company's CA.

### 6.4 Devices – installing and assigning

**Self-registration.** An agent that has a broker and the device secret but no device certificate creates its own:
an RSA key in `LocalMachine\My` (non-exportable) and a self-issued certificate with CN = a new device id – the same
form RemoteHub creates today (agent-spec §5.2). It then sends
`Register {deviceId, certificate, computerName, domain, os, agentVersion, secretId, enrollProof, seal}`. The broker
checks the enroll proof and binds the id to the key for good; the seal it can't check – consoles do. After that the
agent keeps only the record key (to reseal its record when the computer is renamed), DPAPI-encrypted for the machine
in a file only SYSTEM can read; the secret itself isn't kept.

| Situation | How |
|---|---|
| **New install by software distribution** (Intune, GPO, SCCM) | The generic MSI, no enrollment file: `msiexec /i RemoteHubAgent.msi BROKER_URL=rh.contoso.com BROKER_SECRET=rhd1-… /qn`. `BROKER_ONLY=1` also turns the TCP listener off (§10.3). |
| New install from RemoteHub | *Install RemoteHub Agent…* as today. The console registers and seals the device itself, places it in the synced folder where the connection is (§11.4), and puts the broker into the enrollment. |
| **Installed agent, from RemoteHub** | Connection menu → **Assign to RemoteHub Broker**: through the running agent session RemoteHub runs `RemoteHubAgent.exe broker join <url>` with the device secret on standard input (needs the PowerShell right; `agent.json` is writable by administrators only). The agent registers within seconds and the connection is linked to it. |
| **Installed agent, on the device or by script** | `RemoteHubAgent.exe broker join rh.contoso.com` as an administrator, the secret from standard input or a prompt – or the MSI again with the two properties (Intune: repair, or a remediation script). |
| Leaving | `RemoteHubAgent.exe broker leave`, *Remove from RemoteHub Broker* in RemoteHub (it also tells the agent when online), or uninstalling. |

- **The secret stays out of logs and process lists:** `BROKER_SECRET` is an MSI hidden property (`MsiHiddenProperties`),
  never written to the registry or the installer log; `broker join` never takes it as an argument. Intune keeps the
  install command in its own configuration – acceptable for what the device secret allows (§4).
- **Not by policy.** `HKLM\SOFTWARE\Policies` is readable by every local user, so policy can set `BrokerUrl` (§13.1)
  but not the secret.
- **Certificate renewal** (agent-spec §5.4): the agent proves the old key, registers the new one and reseals.

### 6.5 Rotation and leaks

- Several secrets can be active, each with its id – for example one device secret per rollout or customer site:
  `rhbroker secret add device|console`, `rhbroker secret retire <id>`.
- **Retiring a device secret** stops new registrations with it; registered devices keep working (they prove their own
  key). Consoles keep the record keys of retired secrets so old seals stay valid; a new console secret carries every
  active device secret.
- **Retiring a console secret** stops new consoles; registered consoles keep working.
- After a suspected leak: retire, issue a new one, check *New devices* and the console list.

## 7. Control channel

### 7.1 Agent ↔ broker (`/v1/agent`)

- Opened at service start and kept open. Reconnect with exponential back-off from 1 s to 60 s plus random jitter (so
  5,000 agents don't return in the same second).
- **Keep-alive without waking the broker:** the agent sends `ping` every 30 s; the device's Durable Object answers it
  with Cloudflare's WebSocket auto-response, so an idle agent costs no compute (hibernation, §12.2).
- `Register` once (§6.4); then `Hello {agentVersion, os, protocol, features}` on every connect → last seen and public
  address recorded. `Rename {computerName, domain, seal}` when the computer was renamed.
- The broker sends `Incoming` (§8.1); the agent answers `Accept` or `Refuse`. Nothing else in v1: the broker can't make
  the agent do anything but take part in a session it may refuse.

### 7.2 Console ↔ broker (`/v1/console`)

- Open while RemoteHub runs with a broker connected, so the synced folder stays live (§11.4). The tenant's
  `TenantHub` object hibernates between changes, so idle consoles cost nothing.
- **List:** `Sync {sinceVersion}` → the device records, folders and placements changed since then, in pages of
  ≤ 64 KB; the first sync sends everything. After that the broker **pushes** `Changed` (records, folders, placements)
  as they happen.
- **Online state:** `Presence {deviceId, online, lastSeen}` pushed when an agent's control channel comes or goes,
  debounced 10 s so a flapping link doesn't flood the list. Online means the control channel is up (a keep-alive within
  the last 90 s).
- **Writes:** `RegisterDevice` (enrollments from RemoteHub, §6.4), `CreateFolder`, `RenameFolder`, `MoveFolder`,
  `Place {deviceId, folderId, version, seal}`, `Label`, `RemoveDevice`, `Consoles`, `RemoveConsole` – each sealed where
  §6.1 says so.
- `Connect` (§8.1).

## 8. Setting up a peer session

### 8.1 Rendezvous

```
Console                         Broker                                Agent
   │ Connect{deviceId, cands} ─▶ │                                      │
   │                             │ checks console, device online,       │
   │                             │ limits; new session id + key         │
   │                             │ ─ Incoming{session, key, console    ─▶│ checks policy, limits
   │                             │    key, source, cands} ────────      │
   │                             │ ◀─ Accept{session, cands} ───────────│
   │ ◀─ Accepted{session, key,   │                                      │
   │     cands, relayToken} ──── │                                      │
   │ ══ punch + QUIC (direct) ═══╪══════════════════════════════════════▶│   in parallel:
   │ ── relay /v1/relay ────────▶│◀──────────────────── /v1/relay ──────│   relay pairing
```

- `session`: 16 random bytes; `key`: 32 random bytes, sent to both over their control channels, used for the punch
  packets (§8.3) and to bind the relay (§8.5).
- `source`: the console's public address as the broker sees it (`CF-Connecting-IP`) – for the agent's audit and
  sign-in gate.
- The agent refuses (`Refuse {reason}`) when `BrokerEnabled = 0`, too many sessions (`MaxSessions` counts them as
  today), or the console isn't allowed (`BrokerConsoles`, §13.1). The broker passes the reason on.
- 10 s from `Connect` to `Accepted`, else "The device didn't answer through the RemoteHub Broker."

### 8.2 Candidates

Each side gathers, in ≤ 1 s:

- **Host candidates:** its own adapter addresses (IPv4 and global IPv6), without loopback, link-local and Hyper-V's
  internal switches. VPN adapters are kept – a VPN both sides are on is a valid direct path.
- **Server-reflexive:** its public address and port, from a STUN binding request (RFC 8489, binding only) to the STUN
  service (`BrokerStun`, default `stun.cloudflare.com:3478`), sent from the socket that will carry the session.
  Workers can't receive UDP, so the broker itself runs no STUN.
- At most 8 per side. The agent sends only reflexive ones with `BrokerDirect = 1`, none with `0` (relay only).

### 8.3 Hole punching

- Both sides send **punch packets** from their session socket to every candidate of the other side, every 50 ms, for
  at most 3 s: `"RHP1" | session (16) | nonce (8) | HMAC-SHA256(key, …) truncated to 16` – 44 bytes. A valid punch is
  answered with an echo; anything else is dropped without an answer.
- The first pair with a valid answer both ways wins; host candidates are preferred when several answer within 100 ms
  (a LAN path beats a hairpin through the NAT).
- Never to multicast, broadcast or `0.0.0.0`/`::`; at most 200 packets per session.

### 8.4 QUIC over the punched path

- **QUIC from .NET (`System.Net.Quic`, msquic)** on the same local port; the console is the client, the agent the
  server; ALPN `remotehub-peer/1`.
- **Both ends authenticated in the QUIC handshake:** the agent presents its device certificate and the console pins it;
  the console presents a certificate for its console key, and the agent accepts only the key the broker announced in
  `Incoming`. A stranger who finds the punched port gets no further.
- One QUIC stream per peer stream – no head-of-line blocking between the RDP picture and a file copy.
- Idle timeout 30 s with QUIC keep-alive every 10 s (NAT mappings expire after 30–120 s of silence).
- **Windows 11 / Server 2022 or later on both ends** (msquic needs TLS 1.3 in Schannel). Windows 10 and Server 2019
  devices are relay only (§18 D4). How the punched port is handed to msquic is spike B1.

### 8.5 Relay

- Both sides open `wss://broker/v1/relay?session=…` with `Authorization: RemoteHub-Relay <HMAC(key, "relay"|session|side)>`.
  The session's **`RelaySession` Durable Object** pairs the two WebSockets and copies binary messages between them
  **without parsing them** – it never sees more than sizes and timing.
- Inside, the **jump host's framing** (`TunnelProtocol`: `Open`, `Data`, `Credit`, `Close`, `Reset`) multiplexes the
  streams end to end, with an empty `Open` payload (a stream has no target, §5.1). Same flow control: 256 KB window
  per stream; `Data` ≤ 64 KB.
- The relay starts **at the same time** as the punching, so a relayed session loses no time.
- The object is created near the console (Cloudflare places it at the first request); spike B4 measures whether a
  location hint between both sides is worth it.

### 8.6 Choosing the path

- **Up to 3 s for direct.** If QUIC is up within 3 s of `Accepted`, the session is direct; otherwise it uses the relay
  and tries direct again every 30 s.
- **New streams take the best path.** Streams already on the relay stay there until they end – no migration in v1.
- **The agent desktop moves itself:** on a path change it reconnects with its resume token (agent-spec §12) – no new
  consent question, a short freeze.
- **Path lost:** QUIC's connection migration survives a changed console address (Wi-Fi to tethering); if the direct
  path dies, its streams end and the clients reconnect as after a lost jump tunnel – over the relay if needed.

### 8.7 One session per device

Like `JumpTunnels`: one peer session per (console, device), opened on first use, shared by every tab and tool of that
device and of every connection below it, closed 2 minutes after its last stream ends.

## 9. What runs through it

| What | How | Notes |
|---|---|---|
| Agent PowerShell, tools, desktop, health, updates | a stream = the agent's HTTPS endpoint | TLS to the pinned certificate inside the stream. Update: the agent restarts; RemoteHub waits until the device is online at the broker and the new version answers. |
| **RDP to the device itself** | `/v1/tunnel` to its own RDP port (§9.1) | RemoteHub's RDP certificate check and the local forwarder of jump host Phase 2, unchanged. |
| Devices behind it (jump host) | `/v1/tunnel` inside a stream | RDP, WinRM, SFTP, SMB, WebDAV of other devices as jump host §5.5. The jump host itself may be reached through the broker (§10.4). |
| First agent install on a device reached only through the broker | – | needs an agent already: the MSI with `BROKER_URL` and `BROKER_SECRET` by software distribution (§6.4), or *Save the installer and the enrollment file* (jump host §8.4). |

### 9.1 RDP to the device itself

The jump host refuses its own addresses unless the policy names them (jump-host-design §6.2). A device reached through
the broker has no other way in for RDP, so the tunnel gets one narrow exception:

- **`Open` with `host: "self"`** connects to **the device's own RDP listener** – the port RDP is configured for on the
  device (`Terminal Server\WinStations\RDP-Tcp\PortNumber`), whatever port the caller asks for. Nothing else on the
  device's own addresses opens this way.
- Policy `TunnelOwnRdp` (DWORD, default 1 – features on by default); 0 refuses it (`Refused` `policy`, audit 2200
  "(TunnelOwnRdp)"). Right `Tunnel` as for every stream.
- It works for every tunnel, not only through the broker: on a LAN where the firewall allows the agent's port but not
  3389, *Remote desktop through: RDP via the agent* uses it too.
- Which address the agent dials (loopback `127.0.0.2`, its own adapter address) and whether RDP with NLA accepts it is
  spike B6.

## 10. Agent side

### 10.1 `BrokerLink`

The agent service's part: control channel (§7.1), session acceptance, candidates, punching, the QUIC listener for
accepted sessions, the relay client. It reads the policy for every `Incoming` and keeps nothing after a session ends.

### 10.2 Streams into Kestrel

- A **custom Kestrel transport** (`IConnectionListenerFactory` with `IConnectionListenerFactorySelector`, .NET 8+) for a
  `PeerEndPoint`: every peer stream becomes a Kestrel connection next to the TCP listener – same certificate, same
  endpoints. Spike B3.
- **Source address:** `RemoteEndPoint` is the console's public address from `Incoming` (relay) or the QUIC peer address
  (direct). Sign-in gate, audit and `TunnelRules` see it, marked in the audit text
  (`… from 203.0.113.7 via RemoteHub Broker (direct)`). Admins behind the same NAT share one gate – as they do today
  through that NAT.
- Double TLS on the direct path (QUIC's and the stream's) keeps one code path; dropping the inner one is later work.

### 10.3 No listener at all

`ListenEnabled = 0` (policy, §13.1): no TCP port; the service removes its firewall rule at start and restores it when
the value returns to 1. The device is then reachable **only** through the broker. Default 1 (both).

### 10.4 Jump hosts reached through the broker

The jump host rule "the jump host itself must be reachable directly" (jump-host-design §2, §4.3) becomes "… directly
or through the RemoteHub Broker". Nothing else in the tree rules changes. One agent makes a whole site reachable from
anywhere.

## 11. RemoteHub side

### 11.1 Reach – direct or through the broker

A connection setting **Reach**, inherited from folders like the other connection settings, applies to **everything the
connection does** – the agent and RDP alike:

| Reach | Agent (PowerShell, tools, desktop) | RDP |
|---|---|---|
| **Automatic** (default) | direct to the connection's address (TCP, 1.5 s), then the broker | direct to the RDP port (1.5 s), then the broker – RDP through the agent's tunnel (§9.1) |
| **Direct** | direct only – the broker is never used | direct only, as today |
| **Through RemoteHub Broker** | the broker only | through the broker and the agent's tunnel |

- In the same network *Automatic* connects directly and the broker isn't contacted. Which way worked last is remembered
  in memory per connection; the next connect starts with it and falls back to the other.
- A connection below a jump host: *Reach* of the **jump host** decides how the jump host is reached; from there the
  route goes on as today.
- A device in the synced folder (§11.4) is reached directly at the name it reported (`computerName.domain`) and its
  host addresses; its *Reach* is inherited from the synced folder like everywhere else.
- Without a connected broker, *Automatic* is *Direct* and the other choice is off with the reason.
- `TryRoute(connection)` (`MainWindow.JumpHosts.cs`) returns the broker route; `AgentEndpoint.Via` gets a third kind
  next to direct and jump host. `AgentClientTransport.Connect` takes its socket from `PeerSessions` through the same
  `ConnectCallback` the jump host uses; RDP goes through `JumpForwarders` with the `self` target.

### 11.2 Settings → RemoteHub Broker

- **Connect to broker:** address and console secret (or *Import .rhbroker file…*) → **Connect**; then the state
  (connected as "Alice – laptop", broker version, devices, last contact).
- **Test:** control channel, STUN answer, whether UDP gets out – "Direct connections possible" / "Only relayed
  connections: UDP is blocked from this network".
- **Install on devices:** the MSI command line with `BROKER_URL` and `BROKER_SECRET` to copy for Intune or GPO, and
  *Save the agent MSI…* – the secret only shown after a click.
- **Consoles:** the list of registered consoles with last contact, and *Remove*.
- **Disconnect:** removes this console from the broker and the synced folder from the navigation (local overlays are
  kept for a later reconnect).
- Built like the neighbouring settings pages; the layout is settled when it is built.

### 11.3 What the user sees

- Agent and RDP tab headers: "PC-0815 · through RemoteHub Broker (direct)" or "(relayed)"; the tooltip names the path
  (local / internet) and the round trip. Direct connections show nothing new.
- The synced folder with live online state (§11.4); connections in the user's own tree that are linked to a broker
  device get the same online dot.
- No new prompts.

### 11.4 The synced broker folder

Every console connected to the broker shows **one top-level folder for it**, with every registered device:

```
RemoteHub Broker – rh.contoso.com          (synced)
  ├─ New devices          3 new             devices that registered themselves and aren't placed yet
  ├─ Servers
  │    ├─ SRV-SQL01       ● online           Windows Server 2025 · agent 0.22.0
  │    └─ SRV-WEB01       ○ offline 2 h
  └─ Clients
       └─ Berlin
            └─ PC-0815    ● online
```

- **The structure is shared:** folders, device names (labels) and placement come from the broker, sealed (§6.1). Any
  console can create, rename and move folders and drag devices between them; every other console sees it within
  seconds. Devices can't be created here by hand – they register (§6.4).
- **Everything else is the user's own:** credentials (on a device or inherited from a folder inside the synced folder,
  as everywhere), RDP options, colours, *Reach*, notes. Stored in the local database against the device or folder id,
  never at the broker. A shared settings folder doesn't carry them in v1.
- **New devices** is where a self-registered device lands. It is marked *new* for 7 days, and a name that another
  device already has is flagged ("Another device is also called DC01 – check before you sign in"). No credentials are
  inherited into *New devices* unless the user sets some on it themselves; the editor warns when they do.
- **Before the first connect** to a device RemoteHub checks its seal and takes the certificate pin from the sealed
  record. A record without a valid seal is shown greyed – "Not sealed by your organisation" – and can't be opened.
- **Online state** is pushed live (§7.2): ● online, ○ offline with the time since last seen; the tooltip adds OS,
  agent version and the path last used.
- **In the user's own tree:** dragging a broker device into a folder of one's own creates a **linked connection** (it
  follows the device id: name, online state, removal). Existing connections whose agent is registered on the broker
  (same device id) are linked automatically.
- **Removed** at the broker (any console, or `rhbroker device remove`): the device disappears from every synced folder;
  linked connections stay, marked "Removed from the RemoteHub Broker".
- Search and filters include the synced folder.

### 11.5 Errors

Each says which step failed:

- "RemoteHub isn't connected to a RemoteHub Broker. Connect in Settings → RemoteHub Broker, or set Reach to Direct."
- "Can't reach the RemoteHub Broker rh.contoso.com: …" (network, proxy), "This PC was removed from the RemoteHub
  Broker. Connect again with the console secret.", "The console secret is no longer accepted (retired). Ask your
  administrator for the current one."
- "PC-0815 isn't registered on the RemoteHub Broker." (with *Assign to RemoteHub Broker*) / "PC-0815 is offline
  (last seen 3 days ago)."
- "PC-0815's record isn't sealed by your organisation, so RemoteHub doesn't connect to it. The broker may have been
  tampered with – tell your administrator."
- "PC-0815 refused the connection through the RemoteHub Broker: its policy turns it off (BrokerEnabled)." / "… doesn't
  allow this PC (BrokerConsoles)." / "… too many sessions (MaxSessions = 20)."
- RDP: "PC-0815's policy doesn't allow RDP through its agent (TunnelOwnRdp)."
- After that the agent's own errors (pin, sign-in, rights), unchanged.

## 12. The broker

### 12.1 Product

- **Cloudflare Workers in TypeScript** (`broker/` in this repository, built and deployed with `wrangler`). Workers don't
  run .NET, so the wire contract is written down in `docs/broker-protocol.md` with JSON schemas, and conformance tests
  run the C# client side (agent, RemoteHub) against the TypeScript broker (§17.1).
- Licensed AGPL-3.0 like the rest; third-party packages listed in `THIRD-PARTY-NOTICES.md`.
- **Portable core:** routing, authentication, rendezvous and limits are plain TypeScript; storage (`Registry`) and live
  connections (`DeviceHub`, `RelaySession`) are interfaces with a Cloudflare implementation. AWS later implements the
  same interfaces in a container (§16).

### 12.2 Cloudflare pieces

| Piece | Holds |
|---|---|
| Worker (entry) | routing by path, `Challenge`/`Proof`, rate limits, join, registration |
| `DeviceHub` Durable Object, one per device (`tenant:device`) | the agent's control WebSocket (hibernation API – evicted from memory while idle, `ping` answered by auto-response), presence, pending `Incoming` |
| `RelaySession` Durable Object, one per session | the two relay WebSockets; copies messages; byte counters for the audit |
| `TenantHub` Durable Object, one per tenant | the consoles' control WebSockets (hibernating); pushes list changes and presence (`DeviceHub` objects tell it when an agent comes or goes) |
| D1 (SQLite) | tenants, secrets' derived keys, devices, layout, consoles, audit (§12.3) |

### 12.3 Storage

What the shared list needs – computer names, domain and OS of devices – and nothing else: no users, no passwords, no
secrets.

| Table | Columns |
|---|---|
| `tenants` | tenant id, name (one row when self-hosted) |
| `secrets` | tenant, secret id, kind (device / console), enroll or join key, created, retired |
| `devices` | tenant, device id, public key, computer name, domain, OS, agent version, secret id, seal, registered, last seen, last public address |
| `layout` | tenant, folders (id, parent, name), placements and labels – each with version and seal |
| `consoles` | tenant, console id, name, public key, registered, last seen, removed |
| `audit` | tenant, time, event, console, device, addresses, path, bytes, reason – kept 90 days (setting) |

Migrations forward only (pre-GA).

### 12.4 Limits

| Limit | Default |
|---|---|
| Session requests per console | 30 per minute |
| Open peer sessions per console | 200 |
| Relay bandwidth per session | 50 Mbit/s (setting) |
| Control messages per agent | 10 per second |
| Devices per broker | tested to 5,000 (B5) |

### 12.5 Deployment and administration

- A deployment guide: Cloudflare account (MFA, few administrators), a custom domain, `wrangler deploy`, D1 migrations,
  an administrator token, then `rhbroker tenant init` (§6.1) – it prints the device secret and writes the console
  secret into a `.rhbroker` file for the admin PCs.
- Administration through a small CLI `rhbroker` (Node, talks to an admin API with that token, creates secrets locally):
  `tenant init`, `secret add|list|retire`, `console list|remove`, `device list|remove`, `audit --since`. Day-to-day work
  – folders, placing devices, removing consoles – happens in RemoteHub (§11.2, §11.4). No web UI in v1.
- Cost: idle agents hibernate; the spikes measure relay cost per GB and per open session (B4, B5) for the guide.

## 13. Policy

### 13.1 Agent (`HKLM\SOFTWARE\Policies\RemoteHub\Agent`)

| Value | Type | Default | Meaning |
|---|---|---|---|
| `BrokerEnabled` | DWORD 0/1 | 1 | 0: never connect to a RemoteHub Broker (the enrollment's broker is ignored). |
| `BrokerUrl` | SZ | from the enrollment | The broker to use – lets IT move devices to another broker. |
| `BrokerDirect` | DWORD 0–2 | 2 | 0: relay only (no UDP, no candidates); 1: direct over public addresses only; 2: also local addresses. |
| `BrokerStun` | SZ | `stun.cloudflare.com:3478` | STUN service for the direct path. |
| `BrokerConsoles` | MULTI_SZ | any registered console | Console ids allowed through the broker. |
| `BrokerProxy` | SZ | WinHTTP setting | `http://proxy:8080`. |
| `ListenEnabled` | DWORD 0/1 | 1 | 0: no TCP listener; reachable only through the broker (§10.3). |
| `TunnelOwnRdp` | DWORD 0/1 | 1 | 0: no RDP to this device through its own agent (§9.1). |

On by default once the agent has a broker (features on by default; IT turns them off). An unreadable policy key
refuses everything, as today. The ADMX template (admx-design §9) gets a *RemoteHub Broker* category. The device secret
is never a policy value (§6.4); `BrokerUrl` alone moves registered devices to another broker address.

**MSI properties** (agent installer):

| Property | Meaning |
|---|---|
| `BROKER_URL` | The broker, written to `agent.json`. |
| `BROKER_SECRET` | The device secret for self-registration (§6.4) – hidden property, not stored as given. |
| `BROKER_ONLY` | 1: `listenEnabled: false` in `agent.json` – no TCP listener and no firewall rule (§10.3). |

### 13.2 Console (`HKLM\SOFTWARE\Policies\RemoteHub`)

| Value | Meaning |
|---|---|
| `AllowBroker` (feature, admx-design §5) | 0: no broker at all – settings page locked, Reach fixed to Direct. |
| `BrokerUrl` | The broker, locked. |
| `Connections\Reach` | Locks the connection setting (admx-design §7): `Automatic`, `Direct`, `Broker`. |
| `BrokerDirect` | 0: this PC never tries direct paths through the broker (relay only). |

## 14. Audit

### 14.1 Agent (Application log, source *RemoteHub Agent*)

| Event | Id | When |
|---|---|---|
| `BrokerConnected` | 1300 | Control channel up: broker. Once per change, not per reconnect. |
| `BrokerSessionStarted` | 1301 | Session accepted: console id and name, source, path (direct local / direct internet / relayed). |
| `BrokerSessionEnded` | 1302 | Duration, streams, bytes each way, path changes, why. |
| `BrokerSessionRefused` | 2300 | Refused by policy (`BrokerEnabled`, `BrokerConsoles`) or limit. |
| `BrokerRegistered` | 1303 | The device registered itself or was assigned: broker, device id, secret id. Also `broker leave`. |
| `BrokerError` | 3300 | Registration refused (secret retired or unknown, key unknown), broker unreachable for more than an hour (once). |

Sign-ins and streams inside a session are audited as today (1000, 1100, 1200, 2000 …) with the `via RemoteHub Broker`
source; RDP to the device itself is a tunnel stream (1201) with target `self`.

### 14.2 Broker

The `audit` table: console registered/removed, device registered/removed, folder and placement changes (by which
console), secret added/retired, session requested/started/ended (console, device, addresses, path, relayed bytes),
refusals, failed proofs. Never: secrets, derived keys, seals' keys, session keys, relay tokens. Workers logs
(`console.log`) carry no identities beyond ids.

## 15. Technical risks and spikes

Done first, results in §19 before Phase 1:

| Spike | Question | Candidates, in order |
|---|---|---|
| **B1** | Hand a hole-punched UDP port to msquic | 1. Punch with a plain socket, close it, bind msquic to the same local port (the NAT mapping stays). 2. QUIC's own Initial packets do the punching. 3. WebRTC data channels (SIPSorcery, BSD-3-Clause – more code parsing network input as LocalSystem). |
| **B2** | Windows versions for the direct path | Confirm: Windows 11 / Server 2022+; older devices relay only (decided, §18 D4). |
| **B3** | Peer streams as Kestrel connections | 1. Custom `IConnectionListenerFactory` + selector. 2. A named pipe listener (SYSTEM only) with a one-line source header per connection. |
| **B4** | Relay through a Durable Object | 2 GB copy plus an agent desktop at 30 fps through one `RelaySession`: throughput (target ≥ 20 MB/s), added round trip, CPU time and cost per GB; location hint or not. |
| **B5** | 5,000 agents on hibernating `DeviceHub` objects | Reconnect storm after a deploy, auto-response keep-alive not waking objects, cost per month. |
| **B6** | RDP to the device itself from its agent (§9.1) | `127.0.0.2`, then the device's own adapter address; NLA with NTLM, the RDP certificate check of jump host §14. |

## 16. Later: management server, hosted broker, AWS

Kept stable now so these are additions:

- **The organisation's PKI replaces the secrets:** devices get their certificate from the company's CA (Intune
  SCEP/PKCS), consoles a client certificate (Intune, smart cards). Consoles then trust a device because its
  certificate chains to the tenant's CA, not because of a seal; the layout is signed by the console's certificate.
  The secrets stay possible for organisations without a PKI.
- **Users and sign-in:** consoles bound to users, with the PKI above or Entra ID / OIDC where that fits.
- **Who sees what:** device visibility per user or group (RBAC) instead of every console seeing every device
  (§18 D9).
- **Rights from the broker:** the agent already works with a list of rights (agent-desktop-draft §4.1). The broker can
  later issue a **signed rights token** per user and device; an agent policy chooses where rights come from (Windows
  groups, the broker, or both must allow).
- **Hosted, multi-tenant:** the tenant id is in every table and every Durable Object name from v1; the hosted service
  adds tenant sign-up, per-tenant limits and billing.
- **AWS:** the portable core (§12.1) in a container on ECS/Fargate – `Registry` on PostgreSQL or DynamoDB, `DeviceHub`
  and `RelaySession` in process with routing by device id. Not API Gateway WebSockets (frame limits, cost per message).
- **More roles:** device inventory, central audit from agents, policy and update distribution, automatic placement
  rules for new devices, credentials shared through a vault, a web console.

## 17. Phases

| Phase | Content |
|---|---|
| **0 – Spikes** | B1–B6 (§15). |
| **1 – Broker, sharing and relay** | Broker on Cloudflare (§12); secrets, seals, console registration (§6.1–§6.3); self-registration, MSI properties, *Assign to RemoteHub Broker* and `broker join` (§6.4); control channels (§7); the synced folder with live presence (§11.4); relay sessions (§8.1, §8.5); Kestrel transport (§10.2); *Reach* and settings (§11); RDP to the device itself (§9.1); jump hosts through the broker (§10.4); policy, audit, errors. Every console sees every device and reaches it from anywhere – relayed. |
| **2 – Direct** | STUN, candidates, punching, QUIC (§8.2–§8.4), path choice and the desktop move (§8.6), *Test* in settings. |
| **3 – Broker only** | `ListenEnabled = 0` and `BROKER_ONLY` (§10.3), secret rotation in practice (§6.5), ADMX entries. |
| Later | §16, stream migration, inner TLS dropped on QUIC. |

### 17.1 Tests

- **Broker** (`broker/`, Vitest with Cloudflare's Workers test pool – Miniflare, real Durable Objects and D1 locally):
  enroll and join proofs (retired secret, wrong key, replayed nonce, wrong host), an id bound to its first key,
  `Sync` paging and pushed changes, presence debounce, layout versions only going up, rendezvous, relay pairing (wrong
  token, one side missing, a third connection), limits, tenant separation, audit without secrets.
- **Unit** (`tests/RemoteHub.Tests`): the C# side against a broker started locally (`wrangler dev`, skippable when Node
  is missing): key derivation and seals (record and layout; a device can't make a layout seal), agent self-registration
  and its certificate, `broker join` reading the secret from standard input only, the record key kept SYSTEM-only,
  agent and console proofs, framing and flow control through the relay, punch packet format and HMAC,
  candidate filtering (special addresses, `BrokerDirect` levels), path choice with a channel that comes up late,
  `Reach` resolution and inheritance, the `self` tunnel target and `TunnelOwnRdp`, policy parsing.
- **View (STA):** the settings page (connect, import file, disconnect, test results, MSI command line, console list,
  locked by policy); the synced folder (sync, live presence, drag between folders, *New devices* with the new mark and
  the duplicate-name warning, an unsealed device greyed and not openable, linked connections, removal); *Assign to
  RemoteHub Broker*; *Reach* in the editor; tab header path; the errors of §11.5.
- **Lab** (§17.2) and **security** (§17.3).

### 17.2 Lab (`lab/tests/B-BRK.Tests.ps1`, cases in `test-lab-cases.md` as §9.14)

No new VM and no internet: the broker runs on the lab host with `wrangler dev` (local Durable Objects and D1, as
`labop`, port 8787), a small STUN responder in the lab harness on UDP 3478 (`BrokerStun` points to it); the device sits
behind the **jump host VM acting as NAT** (WinNAT, `New-NetNat`, on its private switch `RemoteHub-Jump`), the console
VM on the lab network.

| Id | Test |
|---|---|
| B-00 | The console VM can't reach the device directly (agent port, RDP, WinRM). |
| B-01 | Connect a console with the console secret; a retired console secret is refused. |
| B-02 | Enrollment through a connected console registers the device in the folder of its connection; the agent connects; presence shows it online. |
| B-03 | Relayed (`BrokerDirect = 0`): PowerShell, tools, agent desktop, file copy, agent update; audit 1301/1302 "relayed". |
| B-04 | Direct across the NAT: the same, path "direct internet"; the relay's bytes stay near zero. |
| B-05 | UDP blocked on the NAT VM: relay within 3 s, no error. |
| B-06 | UDP unblocked during a relayed agent desktop: new streams go direct, the desktop resumes on the direct path without a consent question. |
| B-07 | RDP to the device through the broker (`self` target); `TunnelOwnRdp = 0` refuses it with the message and audit 2200. |
| B-08 | *Reach*: Automatic on the LAN connects directly (the broker sees no session); Direct never uses the broker; Through RemoteHub Broker always does. |
| B-09 | Jump host through the broker: RDP and WinRM to a second device behind the broker-reached jump host. |
| B-10 | `ListenEnabled = 0`: no listening port, firewall rule gone, reachable through the broker only; back with 1. |
| B-11 | Policy refusals: `BrokerEnabled = 0`, `BrokerConsoles` without this console – messages and audit 2300. |
| B-12 | Broker restart: agents reconnect with jitter; an open relayed session ends readably and reconnects. |
| B-13 | Proxy: the device's outbound only through an HTTP proxy on the host (`BrokerProxy`) – relayed session works. |
| B-14 | Silent MSI install with `BROKER_URL`, `BROKER_SECRET` (and once `BROKER_ONLY=1`): the device appears in *New devices* on the console, online; the secret is in no installer log, registry value or file other than the SYSTEM-only record key. |
| B-15 | An agent installed without a broker: *Assign to RemoteHub Broker* from RemoteHub, and `broker join` on the device with the secret on standard input – both register; `broker leave` removes it. |
| B-16 | Two consoles: a folder created and a device moved on one appear on the other within 10 s; the device going offline (service stopped) and online again shows on both. |
| B-17 | Retired device secret: a new install with it is refused (3300); devices registered with it keep working. |

### 17.3 Security (`SEC-BRK`, in `security-test-lab-design.md`, tag `Security`)

| Id | Attack | Expected |
|---|---|---|
| SEC-BRK-01 | A console without a registered key, a registration without the console secret, or a replayed proof | refused before any message is handled |
| SEC-BRK-02 | An agent with an unregistered key, or a registered id with another key | refused; broker audit; agent 3300 |
| SEC-BRK-03 | A fake broker sends the console to another agent | pin mismatch, no credentials sent |
| SEC-BRK-04 | A third host sends QUIC to a punched port | handshake refused (not the announced console key) |
| SEC-BRK-05 | Relay with a wrong or reused token, or a third connection to a paired session | refused |
| SEC-BRK-06 | A malicious broker sends `Incoming` with candidates in special ranges or 100 candidates | nothing sent to those; at most 8 candidates, 200 packets |
| SEC-BRK-07 | A stream through the broker tries to reach another host without `/v1/tunnel` | impossible – the framing has no target; a non-empty `Open` ends the session |
| SEC-BRK-08 | The `self` target with another port, or a `self` that the policy forbids | always the device's RDP port / refused |
| SEC-BRK-09 | Session request flood from one console | limited (§12.4); the agent isn't flooded with `Incoming` |
| SEC-BRK-10 | Broker audit and logs after sessions | no secrets, keys or tokens |
| SEC-BRK-11 | Tenant separation: a console of tenant A asks for a device of tenant B | "not registered", nothing reaches the device |
| SEC-BRK-12 | A tampered broker (test build) swaps a device's fingerprint, renames it or moves it into another folder without a valid seal | the console refuses the record or ignores the move; no credentials sent |
| SEC-BRK-13 | With only the device secret: register a device named like an existing one and seal a placement into *Servers* | lands in *New devices*, duplicate name flagged, the placement seal is invalid (no layout key) |
| SEC-BRK-14 | A registered device id registered again with another key | refused; the original device keeps working |
| SEC-BRK-15 | The device secret on the device after install: registry, installer log, `agent.json`, process command lines during `broker join` | not found |

## 18. Decisions (2026-10-26)

| # | Question | Decision |
|---|---|---|
| D1 | Who runs the broker? | Self-hosted first (each organisation's own Cloudflare account). Long term also hosted for customers – tenant id from v1 (§16). |
| D2 | Platform | **Cloudflare-native** (Workers, Durable Objects, D1), no Windows server. AWS later through the portable core (§12.1, §16). |
| D3 | Console identity | ~~Join codes~~ (changed in v3): the organisation's **console secret** now, each console registering its own key (§6.3); long term client certificates from the **organisation's PKI** (§16). |
| D4 | Windows 10 / Server 2019 | Relay only (no QUIC there) – acceptable. |
| D5 | Direct or through the broker | A connection setting **Reach** (Automatic / Direct / Through RemoteHub Broker) for the agent **and RDP**; in the same network the broker isn't needed (§11.1). RDP to a broker-reached device goes through its agent's tunnel with the narrow `self` exception, on by default (`TunnelOwnRdp`, §9.1). |
| D6 | Name | **RemoteHub Broker** – also when the management roles are added. |
| D7 | Trust until the PKI | **Shared secrets** – a device secret (MSI, `broker join`) and a console secret (admin PCs) – with derived keys so the broker can check membership but never forge a device or move one; devices can register but not place themselves (§6.1). No approval step. |
| D8 | Devices in the navigation | **A synced broker folder** per broker: shared structure, own credentials and settings per console; self-registered devices in *New devices* (§11.4). |
| D9 | Who sees which devices | **Every console sees every device** of the organisation; the Windows sign-in and policy on each device still decide who gets in. Per-user visibility with RBAC (§16). |
| D10 | Installing and assigning | Self-registration with `BROKER_URL` / `BROKER_SECRET` (MSI), *Assign to RemoteHub Broker* from RemoteHub, `broker join` on the device; the secret never by policy or on a command line (§6.4). |

## 19. Spike results

(Filled in by Phase 0.)
