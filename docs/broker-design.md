# RemoteHub Broker

Status: Draft v2 (2026-10-26) – open questions decided (§18). Nothing built yet. Phases and tests in §17.
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
  by a key it registered with a one-time join code (§6.2).
- **Devices the console didn't enroll.** The broker lists no devices; RemoteHub still knows its devices from its
  connection list (shared settings work as today).
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
| A stranger uses the broker (connects to devices, uses the relay) | Only registered consoles may ask for a session; joining needs a one-time code from the broker's administrator (§6.2). Relay connections need the session's token (§8.5). |
| A device id is registered by someone who doesn't own it | A device is registered by the console that created its enrollment, with the device key's public part; the agent must then prove that key (§6.3). A device id alone is worth nothing. |
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
│  ├ join, presence   │           │ DeviceHub DO (one per device)│          │  ├ registration, keep-alive   │
│  └ PeerSessions ────┼─relay────▶│ RelaySession DO (per session)│◀─relay───┤  ├ accept / refuse sessions   │
│     ├ RelayChannel  │           │ D1: tenants, devices,        │          │  └ PeerListener ──┐           │
│     └ QuicChannel ◀═╪═══════════╪═ consoles, codes, audit ═════╪═════════▶│                   ▼           │
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

## 6. Identities

The broker runs behind Cloudflare's edge, which terminates TLS, so identities are proven **inside** the WebSocket, not
by TLS client certificates: after the upgrade the broker sends `Challenge {nonce}`, the client answers
`Proof {id, signature}` over `"remotehub-broker/1" | role | id | nonce | broker host`. The broker checks it against the
registered public key (WebCrypto: ECDSA P-256, RSA-PSS). Nothing is accepted before a valid proof; a wrong one closes
the connection.

### 6.1 The broker

- Its name (a Cloudflare custom domain such as `rh.contoso.com`) with Cloudflare's certificate, checked normally by
  chain and name. No pin – Cloudflare rotates its edge certificates.
- This is enough because the broker isn't trusted with anything secret (principle 2).

### 6.2 Consoles

1. The broker's administrator creates a join code: `rhbroker console add --name "Alice – laptop"` → a code like
   `K7Q2-9XMD-4TPA` (80 bits, single use, valid 24 h, stored only as a hash).
2. In RemoteHub, *Settings → RemoteHub Broker*: broker address, join code → **Join**.
3. RemoteHub creates an **ECDSA P-256 key** (non-exportable, CNG, current user) and sends its public part with the
   code. The broker records the console: id, name, public key, joined, last seen.
4. From then on the console proves that key on every control connection (§6).

- **Per PC and user.** Key and console id live in the local database, never in the shared settings file (a shared
  settings folder gives every PC the broker's address, but each PC joins on its own).
- `rhbroker console remove <id>` ends its sessions and refuses it from then on.
- **Long term: the organisation's PKI** (§16): consoles prove a client certificate from the company's CA (Intune
  SCEP/PKCS, smart cards), and the broker trusts that CA instead of keys registered one by one. The challenge–proof
  stays the same; only the proof carries the certificate chain.

### 6.3 Devices

- **Same identity as today:** the device key RemoteHub creates for the enrollment (agent-spec §5.2) also proves the
  agent towards the broker.
- **Registration by the console:** when a joined console creates an enrollment, it registers the device on the broker
  (`RegisterDevice {deviceId, publicKey}`) and writes the broker into the enrollment (`agent.json` →
  `broker: { url }`). The broker accepts an agent only for a registered device id proving that key.
- **Existing agents:** the connection menu gets **Reach through RemoteHub Broker** (for agents installed before, or a
  broker set up later): RemoteHub registers the device and runs `RemoteHubAgent.exe broker set <url>` through the
  running agent session (`agent.json` is writable by administrators only, so this needs the PowerShell right on the
  device). The service picks it up and connects within seconds.
- **Certificate renewal** (agent-spec §5.4) updates the key on the broker in the same step.
- **Removing:** uninstalling the agent from RemoteHub, *Forget on RemoteHub Broker*, or `rhbroker device remove <id>`.

## 7. Control channel

### 7.1 Agent ↔ broker (`/v1/agent`)

- Opened at service start and kept open. Reconnect with exponential back-off from 1 s to 60 s plus random jitter (so
  5,000 agents don't return in the same second).
- **Keep-alive without waking the broker:** the agent sends `ping` every 30 s; the device's Durable Object answers it
  with Cloudflare's WebSocket auto-response, so an idle agent costs no compute (hibernation, §12.2).
- `Hello {agentVersion, protocol, features}` → last seen and public address recorded.
- The broker sends `Incoming` (§8.1); the agent answers `Accept` or `Refuse`. Nothing else in v1: the broker can't make
  the agent do anything but take part in a session it may refuse.

### 7.2 Console ↔ broker (`/v1/console`)

- Opened when needed (a connect, presence of visible connections), closed 2 minutes after the last use.
- `Presence {deviceIds[]}` → `{deviceId: online | offline | unknown, lastSeen}` – for the online dot (§11.3). At most
  1,000 ids per request.
- `RegisterDevice`, `ForgetDevice` (§6.3), `Connect` (§8.1).

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
| First agent install on a device reached only through the broker | – | needs an agent already; *Save the installer and the enrollment file* or software distribution (jump host §8.4). |

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
- Without a joined broker, *Automatic* is *Direct* and the other choice is off with the reason.
- `TryRoute(connection)` (`MainWindow.JumpHosts.cs`) returns the broker route; `AgentEndpoint.Via` gets a third kind
  next to direct and jump host. `AgentClientTransport.Connect` takes its socket from `PeerSessions` through the same
  `ConnectCallback` the jump host uses; RDP goes through `JumpForwarders` with the `self` target.

### 11.2 Settings → RemoteHub Broker

- Address, join code, **Join**; then the state (joined as "Alice – laptop", broker version, last contact), **Test**
  (control channel, STUN answer, whether UDP gets out – "Direct connections possible" / "Only relayed connections: UDP
  is blocked from this network") and **Leave**.
- Built like the neighbouring settings pages; the layout is settled when it is built.

### 11.3 What the user sees

- Agent and RDP tab headers: "PC-0815 · through RemoteHub Broker (direct)" or "(relayed)"; the tooltip names the path
  (local / internet) and the round trip. Direct connections show nothing new.
- Navigation: an online dot for devices registered on the broker (`Presence`, every 60 s while the window is active,
  visible connections only).
- No new prompts.

### 11.4 Errors

Each says which step failed:

- "RemoteHub isn't joined to a RemoteHub Broker. Join one in Settings → RemoteHub Broker, or set Reach to Direct."
- "Can't reach the RemoteHub Broker rh.contoso.com: …" (network, proxy), "This PC was removed from the RemoteHub
  Broker. Join again with a new code."
- "PC-0815 isn't registered on the RemoteHub Broker." (with *Reach through RemoteHub Broker*) / "PC-0815 is offline
  (last seen 3 days ago)."
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
| D1 (SQLite) | tenants, devices, consoles, join codes, audit (§12.3) |

### 12.3 Storage

Only what the broker needs – no device names, no user data:

| Table | Columns |
|---|---|
| `tenants` | tenant id, name (one row when self-hosted) |
| `devices` | tenant, device id, public key, registered by (console id), registered, last seen, last public address, agent version |
| `consoles` | tenant, console id, name, public key, joined, last seen, removed |
| `join_codes` | tenant, hash, name, expires, used |
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
  the first tenant and an administrator token.
- Administration through a small CLI `rhbroker` (Node, talks to an admin API with that token): `console add|list|remove`,
  `device list|remove`, `audit --since`. No web UI in v1 – that is the management server's.
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

On by default once a broker is in the enrollment (features on by default; IT turns them off). An unreadable policy
key refuses everything, as today. The ADMX template (admx-design §9) gets a *RemoteHub Broker* category.

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
| `BrokerError` | 3300 | Registration refused (key unknown), broker unreachable for more than an hour (once). |

Sign-ins and streams inside a session are audited as today (1000, 1100, 1200, 2000 …) with the `via RemoteHub Broker`
source; RDP to the device itself is a tunnel stream (1201) with target `self`.

### 14.2 Broker

The `audit` table: console joined/removed, device registered/removed, session requested/started/ended (console,
device, addresses, path, relayed bytes), refusals, failed proofs and join codes. Never: session keys, relay tokens,
join codes. Workers logs (`console.log`) carry no identities beyond ids.

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

- **Users and sign-in:** consoles bound to users; sign-in with the **organisation's PKI** (client certificates from the
  company's CA – Intune SCEP/PKCS, smart cards; the broker trusts the tenant's CA, §6.2) and with Entra ID / OIDC
  where that fits.
- **Rights from the broker:** the agent already works with a list of rights (agent-desktop-draft §4.1). The broker can
  later issue a **signed rights token** per user and device; an agent policy chooses where rights come from (Windows
  groups, the broker, or both must allow).
- **Hosted, multi-tenant:** the tenant id is in every table and every Durable Object name from v1; the hosted service
  adds tenant sign-up, per-tenant limits and billing.
- **AWS:** the portable core (§12.1) in a container on ECS/Fargate – `Registry` on PostgreSQL or DynamoDB, `DeviceHub`
  and `RelaySession` in process with routing by device id. Not API Gateway WebSockets (frame limits, cost per message).
- **More roles:** device inventory, central audit from agents, policy and update distribution, enrollment tokens for
  mass rollout, a web console.

## 17. Phases

| Phase | Content |
|---|---|
| **0 – Spikes** | B1–B6 (§15). |
| **1 – Broker and relay** | Broker on Cloudflare (§12), join (§6.2), device registration and control channel (§6.3, §7), relay sessions (§8.1, §8.5), Kestrel transport (§10.2), *Reach* and settings in RemoteHub (§11), RDP to the device itself (§9.1), jump hosts through the broker (§10.4), policy, audit, errors. Everything works from anywhere – relayed. |
| **2 – Direct** | STUN, candidates, punching, QUIC (§8.2–§8.4), path choice and the desktop move (§8.6), *Test* in settings. |
| **3 – Broker only** | `ListenEnabled = 0` (§10.3), *Reach through RemoteHub Broker* for existing agents, presence, ADMX entries. |
| Later | §16, stream migration, inner TLS dropped on QUIC. |

### 17.1 Tests

- **Broker** (`broker/`, Vitest with Cloudflare's Workers test pool – Miniflare, real Durable Objects and D1 locally):
  join codes (single use, expiry, hash only), proofs (wrong key, replayed nonce, wrong host), registration,
  rendezvous, relay pairing (wrong token, one side missing, a third connection), limits, tenant separation, audit
  without secrets.
- **Unit** (`tests/RemoteHub.Tests`): the C# side against a broker started locally (`wrangler dev`, skippable when Node
  is missing): agent and console proofs, framing and flow control through the relay, punch packet format and HMAC,
  candidate filtering (special addresses, `BrokerDirect` levels), path choice with a channel that comes up late,
  `Reach` resolution and inheritance, the `self` tunnel target and `TunnelOwnRdp`, policy parsing.
- **View (STA):** the settings page (join, leave, test results, locked by policy), *Reach* in the editor, tab header
  path, online dot, the errors of §11.4.
- **Lab** (§17.2) and **security** (§17.3).

### 17.2 Lab (`lab/tests/B-BRK.Tests.ps1`, cases in `test-lab-cases.md` as §9.14)

No new VM and no internet: the broker runs on the lab host with `wrangler dev` (local Durable Objects and D1, as
`labop`, port 8787), a small STUN responder in the lab harness on UDP 3478 (`BrokerStun` points to it); the device sits
behind the **jump host VM acting as NAT** (WinNAT, `New-NetNat`, on its private switch `RemoteHub-Jump`), the console
VM on the lab network.

| Id | Test |
|---|---|
| B-00 | The console VM can't reach the device directly (agent port, RDP, WinRM). |
| B-01 | Join with a code; the same code again and an expired code are refused. |
| B-02 | Enrollment through a joined console registers the device; the agent connects; presence shows it online. |
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

### 17.3 Security (`SEC-BRK`, in `security-test-lab-design.md`, tag `Security`)

| Id | Attack | Expected |
|---|---|---|
| SEC-BRK-01 | A console without a registered key, or a replayed proof | refused before any message is handled |
| SEC-BRK-02 | An agent with an unregistered key, or a registered id with another key | refused; broker audit; agent 3300 |
| SEC-BRK-03 | A fake broker sends the console to another agent | pin mismatch, no credentials sent |
| SEC-BRK-04 | A third host sends QUIC to a punched port | handshake refused (not the announced console key) |
| SEC-BRK-05 | Relay with a wrong or reused token, or a third connection to a paired session | refused |
| SEC-BRK-06 | A malicious broker sends `Incoming` with candidates in special ranges or 100 candidates | nothing sent to those; at most 8 candidates, 200 packets |
| SEC-BRK-07 | A stream through the broker tries to reach another host without `/v1/tunnel` | impossible – the framing has no target; a non-empty `Open` ends the session |
| SEC-BRK-08 | The `self` target with another port, or a `self` that the policy forbids | always the device's RDP port / refused |
| SEC-BRK-09 | Session request flood from one console | limited (§12.4); the agent isn't flooded with `Incoming` |
| SEC-BRK-10 | Broker audit and logs after sessions | no keys, tokens or join codes |
| SEC-BRK-11 | Tenant separation: a console of tenant A asks for a device of tenant B | "not registered", nothing reaches the device |

## 18. Decisions (2026-10-26)

| # | Question | Decision |
|---|---|---|
| D1 | Who runs the broker? | Self-hosted first (each organisation's own Cloudflare account). Long term also hosted for customers – tenant id from v1 (§16). |
| D2 | Platform | **Cloudflare-native** (Workers, Durable Objects, D1), no Windows server. AWS later through the portable core (§12.1, §16). |
| D3 | Console identity | Join codes now (§6.2); long term client certificates from the **organisation's PKI** (§16). |
| D4 | Windows 10 / Server 2019 | Relay only (no QUIC there) – acceptable. |
| D5 | Direct or through the broker | A connection setting **Reach** (Automatic / Direct / Through RemoteHub Broker) for the agent **and RDP**; in the same network the broker isn't needed (§11.1). RDP to a broker-reached device goes through its agent's tunnel with the narrow `self` exception, on by default (`TunnelOwnRdp`, §9.1). |
| D6 | Name | **RemoteHub Broker** – also when the management roles are added. |

## 19. Spike results

(Filled in by Phase 0.)
