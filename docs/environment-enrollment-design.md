# RemoteHub – Environment enrolment

Status: Draft v1 (2026-11-09), a proposal – nothing is built yet. Written against main as of 0.22.x (agent 0.22, broker
protocol `remotehub-broker/1`). It changes how devices and consoles join RemoteHub and makes the broker the main
connection point; [device-monitoring-design.md](device-monitoring-design.md) builds on it. §16 has the open questions.
Owner: Grischa Ernst

## 1. Goal

Today a device is enrolled in one of two ways: with a console's enrolment file (`.rhagent`, the console's enrolment
secret and a claim, [agent-spec.md](agent-spec.md) §5), or by joining a RemoteHub Broker with its address and a device
secret (`BROKER_URL`, `BROKER_SECRET`, README *RemoteHub Broker*). The broker is optional, every address is typed by
hand, and a new service – device monitoring – would need a third address on every device.

This design makes one way of joining: **an environment URL and an enrolment token**.

```
msiexec /i RemoteHub-Agent.msi ENVIRONMENT=staging.remotehub.dev ENROLLMENT_TOKEN_FILE=C:\Deploy\token.txt /qn
```

The agent asks the environment where everything is – the broker for its tenant's region, the monitoring front door,
the update feed – and keeps asking, so a service can move without touching a device. The broker is no longer
optional: **every enrolled device has a broker connection**, and direct connections on the local network stay what
they are today, the faster path when it is there (*Reach → Automatic*).

| Part | What |
|---|---|
| **Environment** | A RemoteHub deployment with a name people type: `remotehub.dev` (production), `staging.remotehub.dev`, later a customer's own. It publishes a **discovery document** and runs the **enrolment endpoint** (§5). Tenants (today's broker organisations, `o_…`) live in an environment; one environment holds many tenants. |
| **Enrolment token** | A tenant-scoped secret with a name, an expiry, a use cap, revocation and an audit trail, created by the tenant's admin in the console (§4). It is today's device secret given a lifecycle; its wire form stays `rhd1.<org>.<id>.<key>`, so what exists keeps working. |
| **Discovery** | `GET https://<environment>/.well-known/remotehub` and, after the proof, a tenant-specific answer: region, broker, monitoring, updates, STUN, protocol versions, the environment's key (§5). Repeated daily and whenever a service doesn't answer. |
| **The agent** | `enroll` takes the environment and the token (MSI properties, the `.rhagent` file in format 3, or the command line with the token on standard input), makes the device's own key and certificate as today, proves the token to the environment, registers with its region's broker, and writes everything it learned into `agent.json` (§6). |
| **The console** | *Settings → Environment* replaces *Settings → RemoteHub Broker*: an environment URL and a console secret, the same discovery, the tenants' synced folders as today. Devices appear with their pins from the sealed records; the console's own enrolment secret and the claim become the legacy path (§7). |
| **Jump hosts** | A device without a path to the environment joins through its jump host's agent, which relays to the environment's discovered hosts and nothing else – enrolment, the broker connection and monitoring uploads alike (§10). |

### 1.1 Principles

1. **One home for a device: its environment.** The device knows one name and one token at install. Everything else –
   which broker, which region, which service – comes from the environment and can change.
2. **The broker is the main connection point.** Every device is reachable through it from anywhere; direct
   connections on the local network are an optimisation the console tries first, as today.
3. **End to end stays end to end.** The token carries the organisation's key material, the environment stores only
   keys derived from it (the enroll key), and the device's record is sealed with a key the environment never holds –
   exactly the broker's model today ([agent-spec.md](agent-spec.md) §7, `BrokerProtocol.cs`). Discovery tells a device
   *where* to connect; it can't make a console trust a device or a device trust a console.
4. **A token is a thing with a life.** Named, scoped to one tenant, expiring, capped, revocable, every use audited. A
   token on a deployment share is a risk the admin can see and end.
5. **Nothing typed twice.** The environment name is the only address an admin types, on the device and in the
   console. Ports, regions and service hosts are the environment's business.
6. **What exists keeps working.** Devices enrolled with a format-2 file or joined with `BROKER_URL` are not touched by
   an update; the console reads both; the policy names stay. New installs use the new way (§8).

## 2. Non-goals (v1)

- **A customer-hosted environment.** The discovery format and the enrolment endpoint are made so one can exist, but
  installing and running one at a customer is not designed here.
- **Changing sign-in and rights.** Windows accounts, the agent's rights and policies, consent and the desktop are as
  they are. Enrolment decides where a device connects, not who may use it.
- **Removing the legacy path.** Console-enrolled devices without a broker stay supported until a later decision (§8).
- **Agent updates from the environment.** The discovery document names an update feed so [agent-spec.md](agent-spec.md)
  §9 can be built on it later; the feed itself is not designed here.

## 3. Today and proposed

| | Today | Proposed |
|---|---|---|
| What the device is given | A `.rhagent` file with the console's enrolment secret, **or** `BROKER_URL` + a device secret | The environment name and an enrolment token (MSI properties, a format-3 `.rhagent`, or the command line) |
| How the console learns the device | A claim against the device over the network, or the broker's sealed record | The sealed record at the environment's broker, always |
| Where the broker's address comes from | Typed at install or in a policy | Discovery from the environment, per tenant and region |
| Where a new service's address comes from | A new property or policy per service | Discovery |
| A secret's lifecycle | The console's enrolment secret: replace and revoke; the device secret: retire at the broker | The token: name, expiry, use cap, revoke, audit of every use |
| A device without a broker | Normal | Legacy only; new devices always have one |
| Consoles | *Settings → RemoteHub Broker*, address + console secret | *Settings → Environment*, environment + console secret; the same discovery |

## 4. The enrolment token

- **Form:** today's device secret, `rhd1.<org>.<secret id>.<key>` (`BrokerProtocol.Secret`), so every agent and broker
  that exists parses it; the console and the documentation call it an *enrolment token*. The key is 32 random bytes,
  made in the console; the environment receives only the derived enroll key (`DeriveKey(…, Enroll)`), as the broker
  does for device secrets today, and the record key is derived on every device and console from the token itself.
- **Lifecycle at the environment** (new): a name (*Intune rollout Q4*), an expiry (default 90 days, *never* allowed
  and shown in amber), a use cap (default none), the install defaults the console attached (§6.3), who created it, and
  every use: time, device id, source address, agent version. **Revoke** stops new enrolments at once; devices already
  registered keep working (their keys are theirs). **Retire** additionally retires the derived keys as retiring a
  device secret does today.
- **Created in the console:** *Settings → Environment → <tenant> → Enrolment tokens → New…* (`AllowEnvironments` and
  the tenant's admin role). The token is shown once, copied to the clipboard for 2 minutes (as the device secret is
  today), or written into a `.rhagent` file or a token file for `ENROLLMENT_TOKEN_FILE`. The list shows each token's
  uses and last use; a token used from an unexpected network is the admin's to notice.
- **A console secret** (`rhc1…`) gets the same lifecycle (name, expiry, revoke, uses) in the same list, under
  *Console tokens* (§16 Q5).

## 5. Discovery

### 5.1 The document

`GET https://<environment>/.well-known/remotehub` – no credentials, cacheable for an hour, at most 64 KB:

```json
{
  "protocol": "remotehub-env/1",
  "environment": { "id": "env_prod", "name": "RemoteHub", "operator": "RemoteHub" },
  "enrol": "https://<environment>/v1/enrol",
  "console": "https://<environment>/v1/console",
  "regions": ["eu", "us"],
  "keys": [ { "kid": "2026-11", "alg": "ES256", "spki": "…" } ],
  "minAgent": "0.23.0",
  "minConsole": "0.23.0"
}
```

The agent accepts only `https` (http for a lab's local environment, as the broker allows today), checks the protocol
and the sizes, and refuses a document that would send it to another host for `enrol` (the enrolment endpoint is the
environment's own host; a service host may differ, below). The document is signed by one of the environment's keys
(a detached ES256 signature header over the canonical body, `BrokerProtocol.Canonical`), and the agent keeps the keys it
saw, so a changed key is noticed and logged even though TLS is the first line.

### 5.2 Enrolment: the tenant's answer

`POST https://<environment>/v1/enrol` with `{org, secretId, deviceId, fingerprint, computerName, dnsHostName, nonce,
spki, proof}` – the proof is the broker registration's proof of today (`ProofText`, HMAC with the enroll key), with
the device's public key added so the environment registers it at the broker in the same step. The answer, signed with
the environment's key:

```json
{
  "org": "o_…", "region": "eu", "tenantName": "Contoso",
  "broker":     { "url": "https://eu.broker.remotehub.dev", "stun": "stun.remotehub.dev:3478" },
  "monitoring": { "url": "https://eu.monitoring.remotehub.dev", "entitled": true },
  "updates":    { "url": "https://updates.remotehub.dev/agent" },
  "rediscoverHours": 24,
  "issued": "2026-11-09T09:00:00Z"
}
```

- Refusals: 401 (proof), 403 with a reason (token expired, revoked, over its cap, tenant disabled), 426 (agent older
  than `minAgent`), 429. Each is audited at the environment for the tenant and logged on the device (2401).
- **Rediscovery:** the agent asks again every `rediscoverHours` (24; 1–168), after three failed connections to a
  service in a row, when the broker closes with a new code `4301 Moved`, and at every service start – with the same
  proof, so a revoked token also ends rediscovery (the device keeps working with what it has until the admin acts;
  §9). A changed answer is applied without a restart: the broker link reconnects to the new address, monitoring
  uploads go to the new front door.
- **What discovery can and can't do:** it names hosts. A hostile environment or a spoofed answer could send a device
  to a broker of its choosing, but that broker holds nothing the device trusts without the record key – sessions
  through it still need the console's sealed offer, and the device still checks the console's proof. It can deny
  service; it can't take a session. The policy `Environment` (§9) fixes which environment a device may ask at all.

### 5.3 The console's discovery

The console asks the same document and `POST /v1/console` with its console secret's proof, and receives the same
shape: broker, monitoring API, region, tenant name – plus the keyring blob it gets from the broker today. One
environment with several tenants gives several answers, one per connected tenant.

## 6. The agent

### 6.1 `enroll`

`RemoteHubAgent.exe enroll` (the MSI's deferred custom action, as today) gains the environment inputs and keeps the
steps of [agent-spec.md](agent-spec.md) §5.3:

1. Delayed start; the device's identity (device id, RSA-3072 CNG key, certificate) as today.
2. **Environment:** read the environment and the token – from the `.rhagent` file (format 3, §6.3), from the MSI
   properties `ENVIRONMENT` and `ENROLLMENT_TOKEN_FILE` (or `ENROLLMENT_TOKEN`, hidden), or from the command line with
   the token on standard input (`enroll --environment <url>`). Fetch the discovery document, send the proof (§5.2),
   write the answer and the derived keys into `agent.json` (§6.2). The token itself is zeroed and never kept; the
   token file is deleted as the `.rhagent` file is.
3. Firewall rule and listener as today (`ListenEnabled`, the scope from the file).
4. Virtual display and automatic sign-in as chosen.

A failure at step 2 fails the install with the environment's reason (*The enrolment token has expired – create a new
one in RemoteHub under Settings → Environment*), and rollback runs `cleanup` as today. Without an environment and
without a format-2 file, a first install fails with a message saying what to give it.

### 6.2 `agent.json`

The `broker` object becomes `environment`:

```json
"environment": {
  "url": "https://staging.remotehub.dev",
  "org": "o_…", "secretId": "s_…",
  "enrollKey": "…", "recordKey": "…",
  "region": "eu",
  "broker": { "url": "https://eu.broker.staging.remotehub.dev", "stun": "…" },
  "monitoring": { "url": "https://eu.monitoring.staging.remotehub.dev" },
  "updates": { "url": "…" },
  "keys": [ { "kid": "2026-11", "spki": "…" } ],
  "discoveredUtc": "…", "rediscoverHours": 24
}
```

An `agent.json` with the old `broker` object is read as an environment whose URL is the broker's, with discovery
tried there (§8). `RemoteHubAgent.exe environment status | rediscover | leave` replace `broker status | leave`;
`broker join <url>` stays as an alias that treats the URL as an environment.

### 6.3 The enrolment file, format 3

`AgentEnrollment` keeps its install settings (label, port, allowed groups, firewall scope, virtual display, and the
monitoring defaults of [device-monitoring-design.md](device-monitoring-design.md) §11.3) and replaces the console's
`keyId` and `secret` with `environment` and `token`. The console writes it from *Install RemoteHub Agent* and from
*Settings → Environment → Save enrolment file…*, choosing a token (or making one). A format-2 file is still read by the
agent and still claimed by the console, for the legacy path; a format-1 file stays refused.

### 6.4 After enrolment

The service connects to its region's broker as `BrokerLink` does today (the enroll key, the sealed record, 1300 and
1303), serves direct connections when `ListenEnabled`, uploads monitoring bundles to the front door it was given once
the tenant activates monitoring, and rediscovers on the schedule of §5.2. Health (`/v1/health`) gains
`environment: {id, region}` – names only.

## 7. The console

- **Settings → Environment** replaces the *RemoteHub Broker* page: *Connect* with an environment URL and a console
  secret (or an `.rhbroker` file, which gains the environment URL), the discovery answer shown (region, broker,
  monitoring, tenant name), the registered consoles, the enrolment and console tokens (§4), *Save enrolment file…*,
  *Disconnect*. Several environments and tenants can be connected, as several brokers can today.
- **Devices** appear in the tenant's synced folder with their pins from the sealed records, as broker devices do
  today. *Claim RemoteHub Agent…*, the console's enrolment secret and `AgentEnrollmentKeys` stay for legacy devices and
  are shown under *Legacy enrolment* in the same page, with a note that new devices don't need them.
- **Install RemoteHub Agent** writes a format-3 file with a chosen token; *Assign to RemoteHub Broker…* becomes **Join
  environment…** and does for an installed legacy agent what the MSI does for a new one, over the agent's PowerShell
  as today, with the token passed as a file that is deleted afterwards.
- The connection editor shows a device's environment, tenant and region on its agent page; *Connect through* is the
  agent for environment devices, as for broker devices today.

## 8. Legacy and migration

| Device | After the update | Later |
|---|---|---|
| Console-enrolled, no broker (format-2 file, claim) | Unchanged. Works directly and through jump hosts. Not monitorable (monitoring needs the environment). | *Join environment…* moves it; the legacy path is removed in a later major version after a deprecation notice in the console |
| Joined a broker with `BROKER_URL` + device secret | The agent treats the broker as its environment and tries discovery at `https://<broker>/.well-known/remotehub`. The broker, now part of an environment, answers; the agent fills in the rest of `environment`. Nothing to do on the device | – |
| New install | Needs an environment and a token, or a format-2 file for the legacy path (still allowed in v1, logged as legacy) | Format 2 refused for new installs in a later version |

Policies: `BrokerUrl` and `BrokerEnabled` keep their meaning (`BrokerUrl` is read as an allowed environment as well);
the new `Environment` wins where both are set (§9). The `RdpManager` rule applies: old names stay readable.

## 9. Policy

Agent (`HKLM\SOFTWARE\Policies\RemoteHub\Agent`, ADMX category *RemoteHub Agent → Environment*):

| Value | Type | Meaning (default) |
|---|---|---|
| `Environment` | SZ | The only environment the device may enrol in and rediscover from; an install with another is refused (any) |
| `EnvironmentKeys` | MULTI_SZ | Key ids or SPKI hashes the environment's signatures must come from – a pin for organisations that want one (the keys discovery delivered) |
| `EnvironmentRelay` | DWORD | 0: this device never relays other devices' environment traffic (§10) (1 when `TunnelEnabled` is on) |
| `BrokerEnabled`, `BrokerUrl`, `BrokerConsoles`, `BrokerHttpProxy`, `BrokerKeepAliveSeconds`, `BrokerDirect`, `BrokerStun`, `ListenEnabled`, `TunnelOwnPorts` | as today | `BrokerUrl` doubles as an allowed environment; `BrokerStun` overrides the discovered STUN; `BrokerHttpProxy` applies to every environment connection |

Console: `AllowEnvironments` (MULTI_SZ, the environments this PC may connect to; any) beside `AllowBroker`.

## 10. Jump hosts: the environment relay

A device on a network without a path to the environment – a lab segment, an OT network, a customer site behind a
single jump host – becomes a full member through its jump host's agent, which already runs the tunnel
([jump-host-design.md](jump-host-design.md)):

- The console, which knows the chain from the tree, gives the device an **upstream** with its enrolment (the jump
  host's address, port and certificate fingerprint; in the format-3 file or the MSI property `UPSTREAM`). The device
  reaches the environment through `https://<upstream>:58732/v1/environment/forward/<host>/<path>` – discovery, the
  enrolment proof, the broker WebSocket and monitoring uploads alike – checking the upstream's pin as the console does.
- The jump host's agent forwards **only to the environment's discovered hosts** (its own `environment` object: the
  environment, its broker, monitoring and update hosts), byte for byte, TLS from the device to the environment running
  inside the forward, so the jump host sees sizes and hosts, not content. Anything else is refused (2202). Limits:
  `EnvironmentRelay`, at most 64 concurrent forwards per source and 1,024 per jump host, sources within
  `TunnelTargets` when set, special addresses refused as for tunnels. Audit 1203 per source and hour.
- Chains work hop by hop; the console builds the upstream list from the tree, and re-sends it when a device is moved
  (the move's confirmation says so).
- This replaces the monitoring-only relay of [device-monitoring-design.md](device-monitoring-design.md) §9.3, which
  becomes one use of it.

Until the relay is built (phase 3), a device without a path to the environment stays on the legacy path.

## 11. Security

- **What a stolen token can do:** enrol a rogue device into the tenant until the token expires, hits its cap or is
  revoked. The device appears in *New devices* with its own identity; no console connects to it with credentials
  unless an admin moves it into a folder and uses it, and no existing device's identity can be taken (every device
  makes its own key, SEC-ENR-03). The audit of uses and the cap make a leak visible and bounded. This is today's
  device-secret exposure with a lifecycle around it.
- **What the environment holds:** derived enroll keys, device public keys and sealed records, token metadata and
  audit, tenants' regions and entitlements. Never a token's key, a record key, a console secret or a credential.
- **Discovery spoofing:** TLS to a public host name is the first line; the environment's signature over the document
  and the answer the second; `Environment` and `EnvironmentKeys` the third for those who want a pin. A spoofed answer
  can deny service, not open a session (§5.2).
- **The token on the device:** from a file or standard input, never a command line (process-creation events would
  record it; `ENROLLMENT_TOKEN` exists for tools that can't write a file and is marked hidden in the MSI log as
  `BROKER_SECRET` is); zeroed after the proof; the file deleted. Lab case E-ENR-04 checks nothing is left.
- **The relay is not a proxy:** fixed destination set from the jump host's own discovery, no CONNECT to arbitrary
  hosts, limits per source. SEC-ENV-02 tries.
- **Threat boundaries** TB2/TB3 (identity, enrolment) in [security-test-lab-design.md](security-test-lab-design.md) get
  the token and discovery; the technology inventory gets the environment under §2.

## 12. Audit

Agent: 1304 `EnvironmentEnrolled` (environment, tenant, region, token id – never the token), 1305
`EnvironmentRediscovered` when something changed (what moved), 1203 `EnvironmentForwarded` per source and hour, 2401
`EnrolmentRefused` (the environment's reason), 2202 `EnvironmentForwardRefused`, 3401 `EnvironmentError`.
Environment, per tenant: token created, revoked, retired; each use (device id, source, agent version); each refusal;
console connected. Shown in the console under *Settings → Environment → Audit*.

## 13. Tests

Unit (`tests/RemoteHub.Tests`): the discovery document and answer parsed within their limits and refused outside them;
a wrong signature refused, an unknown key logged; `enrol` from the MSI properties, the format-3 file and standard
input; a token on the command line refused unless `ENROLLMENT_TOKEN`; format 2 still read, format 1 refused; the old
`broker` object migrated; the `Environment` policy refusing another; the relay's destination set and limits.

Lab (`docs/test-lab-cases.md` *Agent – environment (E)*; the lab's broker gains the discovery document and the enrol
endpoint, test-lab-spec §5.3):

| ID | Case | Pass when | How | P |
|---|---|---|---|---|
| E-ENR-01 | MSI with the environment | `ENVIRONMENT` + `ENROLLMENT_TOKEN_FILE` → installed, `agent.json` has `environment` with broker and monitoring from discovery, registered and online at the lab broker, 1304 and 1300 audited, token file gone | PS E | 1 |
| E-ENR-02 | Format-3 file | the console's *Save enrolment file…* with a token → the MSI next to it enrols the same way; the device appears in the tenant's synced folder with its pin | H PS | 1 |
| E-ENR-03 | Token refused | expired, revoked, over its cap, another tenant's → the install fails with the environment's reason, rollback clean, 2401 audited; the environment's audit lists the attempt | PS H | 1 |
| E-ENR-04 | Nothing left | no token in `agent.json`, the log, the MSI log, process-creation events (4688 with command-line auditing on) or temp folders | PS E | 1 |
| E-DISC-01 | Rediscovery | the lab environment changes the tenant's broker URL → the agent reconnects to the new one within `rediscoverHours` set to 1, 1305 audited; the broker's `4301 Moved` makes it immediate | H PS | 1 |
| E-DISC-02 | Signature and policy | a document with a bad signature refused; `Environment` set to another name → enrol refused; `EnvironmentKeys` pinned to a wrong key → refused and logged | PS | 1 |
| E-LEG-01 | Legacy devices | a device joined with `BROKER_URL` + device secret before the update → after it, `environment` filled from discovery, same device id and keys, still online; a console-enrolled device without a broker works as before and is marked legacy in the console | H PS | 1 |
| E-CON-01 | Console | **2VM**: *Settings → Environment → Connect* with environment and console secret → tenants' folders, devices with pins, a token created, shown once and used by E-ENR-01's install; *Join environment…* moves a legacy device | U H | 1 |
| E-RELAY-01 | Through a jump host | **2VM**, the target without a path: `UPSTREAM` in the file → enrolled, online at the broker and uploading monitoring through the jump host; 1203 on the jump host; its log has no content | H N | 1 |
| SEC-ENV-01 | Rogue device | a token used from the attacker VM → the device lands in *New devices*, no console connects to it with credentials, the cap and revoke stop further uses, the audit shows the source | H | 1 |
| SEC-ENV-02 | Relay is not a proxy | forwards to a host outside the jump host's discovered set, a CONNECT, a body over the limit, a 65th concurrent forward → refused (2202), nothing sent | H N | 1 |

## 14. Phases

| Phase | Content |
|---|---|
| **1 – Environment on the device and the environment side** | Discovery document and enrol endpoint at the environment (the broker's repository); `enroll` with `ENVIRONMENT`/`ENROLLMENT_TOKEN_FILE`, the format-3 file, `agent.json` `environment`, rediscovery, migration of `broker`, policies `Environment`, `EnvironmentKeys`; audit 1304/1305/2401/3401; the lab environment |
| **2 – The console** | *Settings → Environment*, tokens with their lifecycle and audit, `.rhbroker` with the environment, *Save enrolment file…* format 3, *Join environment…*, the legacy page, `AllowEnvironments`, the device's environment in the editor |
| **3 – The relay** | `/v1/environment/forward`, upstreams from the tree, `EnvironmentRelay`, 1203/2202; monitoring's relay folds into it |
| Later | Agent updates from the environment's feed ([agent-spec.md](agent-spec.md) §9); console tokens' lifecycle if not in phase 2; a customer-hosted environment; removing the legacy path |

Monitoring's phase 1 ([device-monitoring-design.md](device-monitoring-design.md) §17) needs this phase 1: a device
uploads to the front door discovery gave it.

## 15. Docs in step

[agent-spec.md](agent-spec.md) §5 (enrolment rewritten around the environment, the file format 3, the legacy path),
§7 (`environment` in `agent.json`, the policies), §8 and §11; README *RemoteHub Agent* and *RemoteHub Broker* →
*Environment*; `PRIVACY.md` (what the environment holds); [admx-design.md](admx-design.md) §9 and §5;
[jump-host-design.md](jump-host-design.md) §6 (the relay); [threat-model-technologies.md](threat-model-technologies.md)
and [security-test-lab-design.md](security-test-lab-design.md) (TB2/TB3, SEC-ENV); `test-lab-spec.md` §5.3;
`installer/…/Package.wxs` header; [device-monitoring-design.md](device-monitoring-design.md) §4, §6.5, §9.3, §12.

## 16. Open questions

| # | Question | Proposal |
|---|---|---|
| Q1 | **Token expiry default** – 90 days, or none for Intune rollouts that run for a year? | 90 days, *never* allowed and shown in amber with the uses count; Intune admins make a new token a year. |
| Q2 | **Keep the `rhd1` wire form** or introduce `rhe1` for tokens? | Keep `rhd1`: every existing agent and broker parses it, and the lifecycle is metadata at the environment, not in the string. |
| Q3 | **Listener default for new installs** – keep the inbound port and firewall rule on (direct path first), or broker-only by default now that every device has a broker? | Keep it on, scoped by the file's firewall addresses: the direct path is faster on the local network and nothing new is exposed. `ListenEnabled` and `BROKER_ONLY` stay for the other choice. |
| Q4 | **Tenant creation** – an environment-level token that lets an MSP's installer create tenants, or tenants only through the console? | Console only in v1; MSP flows are a later design. |
| Q5 | **Console secrets** – the same lifecycle (name, expiry, revoke, uses) in phase 2, or later? | Phase 2: the same list, the same code. |
| Q6 | **`UPSTREAM` in the file** – the file is per fleet, the upstream per site. One file per site, or the console sets the upstream after enrolment through the jump host's tunnel? | One file per site in v1 (Intune already targets by group); the console's *Monitoring settings…* can change it later. |
| Q7 | **Deprecation of the legacy path** – when is format 2 refused for new installs? | Two minor versions after phase 2 ships, announced in the console's update notes. |

## 17. Decided

| Decision | Settled by |
|---|---|
| The broker is mandatory for new devices; direct connections stay as the first try | The owner: the broker is the main device connection point |
| Environment URL + enrolment token replace both enrolment paths | The owner |
| The agent learns broker, monitoring and update hosts by discovery and rediscovers daily and on failure | The owner's request that the agent get its information from the environment; services can then move |
| The token is today's device secret with a lifecycle; its wire form stays | Compatibility: existing agents and brokers parse it; the end-to-end key model is unchanged |
| The environment stores only derived keys and public keys | The broker's model today; discovery names hosts and can't grant trust |
| Legacy devices keep working; the console keeps the claim for them | Principle 6; removal is a later decision (Q7) |
| The jump-host relay forwards to the environment's discovered hosts only | Not a proxy; one relay for enrolment, broker and monitoring |
