# RemoteHub – The environment: devices and consoles join it

Status: Draft v4 (2026-11-10), a proposal – nothing is built yet. Written against main as of 0.22.x (agent 0.22, broker
protocol `remotehub-broker/1`). v1 made the broker the main connection point and gave devices an environment URL and
an enrolment token. v2 does the same for consoles: **a person signs in to the environment, and the console gets
everything from there** – no console secret, no joining a broker. That moves the trust anchor (§4), so the broker
protocol becomes v2. v3 closes the open questions: **roles grant agent rights** (RBAC, §8.5), tenants start as a
**14-day trial with payment details** and licences bought by the admin or assigned by sales (§8.6), legacy formats go
with phase 2, and relayed devices get a wake channel in phase 3. v4 adds the **tenant key** (§8.7): with it the environment
alone can add neither a device nor a person the tenant trusts. [device-monitoring-design.md](device-monitoring-design.md)
builds on this document. §17 has what is still open.
Owner: Grischa Ernst

## 1. Goal

Today a device is enrolled with a console's enrolment file and a claim, or joins a RemoteHub Broker with its address
and a device secret; a console joins the same broker with a console secret ([agent-spec.md](agent-spec.md) §5, README
*RemoteHub Broker*). Every address and every secret is typed by hand, the broker is optional, and a new service would
need a third address on every device and a third secret in every console.

This design makes one way of joining for both sides: **the environment's name, and an identity.**

```
Device:   msiexec /i RemoteHub-Agent.msi ENVIRONMENT=staging.remotehub.dev ENROLLMENT_TOKEN_FILE=C:\Deploy\token.txt /qn
Console:  Settings → Environment → staging.remotehub.dev → Sign in  (the admin's own account)
```

The agent asks the environment where everything is – the broker for its tenant's region, the monitoring front door,
the update feed – and keeps asking. The console signs the admin in, learns which tenants they belong to with which
role, and receives the devices, folders, entitlements and service addresses of each – no secret to paste, nothing to
register. The broker is no longer optional: every device that can reach the environment has a broker connection, and
direct connections on the local network stay the faster first try (*Reach → Automatic*). A device behind a jump host
has no broker connection: consoles reach it through the jump host as today, and only its enrolment, its monitoring
uploads and a periodic poll go through the jump host's relay (§7.5).

| Part | What |
|---|---|
| **Environment** | A RemoteHub deployment with a name people type: `remotehub.dev` (production), `staging.remotehub.dev`, later a customer's own. It publishes a **discovery document**, runs the **enrolment endpoint** for devices and the **sign-in** for people, and holds the tenants (§6). One environment holds many tenants. |
| **Tenant** | Today's broker organisation (`o_…`): devices, folders, entitlements, a region, and now **members** – people with a role (§8.2). |
| **Enrolment token** | A tenant-scoped secret for devices with a name, an expiry, a use cap, revocation and an audit trail, created by a tenant admin in the console (§7.1). |
| **Discovery** | `GET https://<environment>/.well-known/remotehub`, then a tenant-specific answer after the device's proof or the person's sign-in: region, broker, monitoring, updates, STUN, protocol versions, the environment's keys (§6). Repeated daily and whenever a service doesn't answer. |
| **The agent** | `enroll` takes the environment and the token, makes the device's own key and certificate as today, proves the token, registers its key and its **self-signed record**, connects to its region's broker, and keeps what it learned in `agent.json` (§7). |
| **The console** | Signs the admin in (the tenant's identity provider or a RemoteHub account), keeps a per-console key pair as today, and gets from the environment the tenants, their devices with pins, the folders, the entitlements and the services. Sessions to devices are **attested by the environment** for this person and device, and the device still asks for Windows credentials (§8). |
| **Legacy** | Devices and consoles on the broker today keep working through a migration the environment runs (§9). |

### 1.1 Principles

1. **One home: the environment.** A device knows one name and one token at install; a console knows one name and one
   person. Everything else – brokers, regions, services, tenants, devices – comes from the environment and can change.
2. **People, not secrets, for consoles.** A console belongs to a person who signed in, with a role in a tenant. Access
   ends when the person's account is disabled or their membership removed, and the audit names the person – on the
   environment and on the device.
3. **The broker is the main connection point** for devices that can reach the environment; the jump host remains the
   way to those that can't (§7.5), and direct connections the first try on the local network.
4. **The environment is trusted for membership, devices stay their own witnesses.** The environment says which
   devices and people belong to a tenant. A device's record – its id, name, fingerprint, addresses – is **signed by the
   device's own key**, which never leaves it, so no one, the environment included, can change an existing device's
   pin or speak as it. What a person may do on a device is decided on the device – by the Windows sign-in as today, or by the device's
   policy mapping the attested role onto the agent's rights (§8.5) – never by the environment alone (§4).
5. **A token is a thing with a life.** Named, scoped to one tenant, expiring, capped, revocable, every use audited.
6. **Nothing typed twice.** The environment name is the only address anyone types.
7. **What exists keeps working.** Devices on a broker today and the consoles that joined it are migrated by the
   environment, not reinstalled (§9); console-enrolled devices without a broker stay the legacy path for now.

## 2. Non-goals (v1)

- **A customer-hosted environment.** The formats allow it; running one at a customer is not designed here.
- **Replacing Windows sign-in.** Roles grant agent rights (RBAC, §8.5), but a tenant can keep Windows credentials
  as the only way in by policy, and the agent's rights, consent and desktop rules are unchanged in what they allow.
- **The commerce engine.** Trials, licences and payment are sketched as a data model and a flow (§8.6); the payment
  provider, invoicing and tax are a design of their own.
- **Agent updates from the environment.** Discovery names an update feed for [agent-spec.md](agent-spec.md) §9 later.
- **Shared credentials between admins.** Credentials, RDP options and notes stay the console's own, as in synced
  folders today; sharing them is a separate design.

## 3. Today and proposed

| | Today | Proposed |
|---|---|---|
| A device is given | A `.rhagent` file with the console's enrolment secret, **or** `BROKER_URL` + a device secret | The environment name and an enrolment token |
| A console is given | The broker's address and a console secret (or an `.rhbroker` file) | The environment name; the admin signs in |
| Who may see a tenant | Whoever holds a console secret | Members of the tenant, by role |
| How a console trusts a device's pin | The device's record, sealed with a key derived from the device secret, which consoles get from the keyring | The device's record, **signed by the device's own key**, listed for the tenant by the environment |
| How a device trusts a session request | A session offer sealed with the record key; policy `BrokerConsoles` by console id | A request signed by the console's key with the environment's **attestation** of the person, tenant and role; policy `BrokerUsers`/`BrokerRoles`; then Windows credentials as today |
| Where addresses come from | Typed | Discovery |
| A secret's lifecycle | Replace and revoke; retire at the broker | Device tokens: name, expiry, cap, revoke, audit. Consoles: none – people have accounts |
| The audit on the device | The console's id | The person (UPN) and the console's id |

## 4. The trust model, and what changes

Today's broker never holds the keys that decide where credentials go: the record key and the keyring key are derived
from secrets only devices and consoles have, so a compromised broker can deny service but can't redirect a console
to a rogue device. **Signing people in moves that line.** A console that signs in holds no shared secret, so whatever
proves a device's record to it must come from the environment, and the environment becomes trusted for *membership*:
which devices and people are in a tenant.

What this design keeps, and why it is enough:

| Property | Today | Proposed |
|---|---|---|
| An existing device's pin can't be changed by the service | The record is sealed with the record key | The record is **signed by the device's own key**; the environment stores the key at enrolment and can't sign with it. A changed pin means a changed key means a *new* device |
| A rogue device can't take an existing device's place | Same | Same: a new device appears in *New devices* with a new id; a console's connection to the existing device keeps its pin and refuses the newcomer |
| A rogue device can't be slipped into a tenant unnoticed | Needs the device secret | Needs an enrolment token – or a compromised environment, which can register a device as if a token were used. **This is the new exposure:** it shows as a new device in the tenant's folder, under *New devices*, never as an existing one; no credentials go to it until an admin uses it; the tenant's audit shows a registration without a token use. A tenant that turns on the **tenant key** (§8.7) closes it: nothing the environment adds is trusted until a holder of the key co-signs it |
| A console can't be used without the person | The console secret | The person's sign-in, the console's key, an attestation that expires within hours (§8.4) |
| The device decides who may do what | Windows sign-in, rights, policy | The device's policy decides whether an attested role is enough (RBAC, §8.5) or Windows credentials are still asked; either way the decision and the mapping are on the device, in `Rights`, and the person is in the audit |
| The service reads no session content | TLS end to end to the device's pin | Unchanged |

So the environment can, at worst, add a device or a person to a tenant – and not even that where the tenant key is
on (§8.7); it can't impersonate an existing device or read a session. **With RBAC on (§8.5) adding a person is adding an operator of every device whose policy accepts the
role**, so a tenant's device policy, not the environment, is where that reach is bounded – `Rights` names which roles
count, `BrokerUsers` which people, and both default to the tenant's members with the role the device's `Rights` map. That is the model most device-management services run on, said plainly in the README
and `PRIVACY.md` (§15). The seals and the keyring of `remotehub-broker/1` go; the broker protocol becomes **v2** (§10).

## 5. Terms

| Term | Meaning |
|---|---|
| Environment | A deployment, by name (`staging.remotehub.dev`). Discovery, enrolment, sign-in, tenants, brokers per region, the monitoring front door |
| Tenant | An organisation in an environment (`o_…`), with devices, folders, members, entitlements and a region |
| Member | A person in a tenant with a role: *Owner*, *Admin*, *Operator*, *Viewer* (§8.2) |
| Identity provider | Where the person's account lives: the tenant's Entra ID (or another OpenID Connect provider), or a RemoteHub account (e-mail and passkey) for tenants without one |
| Console | One installation of RemoteHub for one Windows user, with its own key pair, registered to the environment at the person's first sign-in on it |
| Enrolment token | A tenant's device secret with a lifecycle (§7.1) |
| Attestation | A short-lived statement signed by the environment: this console key, for this person with this role in this tenant, may ask this device for a session (§8.4) |
| Record | A device's self-signed description: id, tenant, name, fingerprint, addresses, upstream (§7.3) |

## 6. Discovery

### 6.1 The document

`GET https://<environment>/.well-known/remotehub` – no credentials, cacheable for an hour, at most 64 KB:

```json
{
  "protocol": "remotehub-env/1",
  "environment": { "id": "env_prod", "name": "RemoteHub", "operator": "RemoteHub" },
  "enrol": "https://<environment>/v1/enrol",
  "signIn": { "issuer": "https://<environment>/v1/auth", "clientId": "remotehub-console", "deviceCode": true },
  "console": "https://<environment>/v1/console",
  "regions": ["eu", "us"],
  "keys": [ { "kid": "2026-11", "alg": "ES256", "spki": "…" } ],
  "minAgent": "0.23.0",
  "minConsole": "0.23.0"
}
```

Only `https` (http for a lab's local environment, as the broker allows today); protocol and sizes checked; `enrol`
and `signIn` must be on the environment's own host. The document is signed by one of the environment's keys (a
detached ES256 signature over the canonical body, `BrokerProtocol.Canonical`), and both agent and console keep the keys
they saw, so a changed key is noticed and logged even though TLS is the first line.

### 6.2 The device's answer

`POST https://<environment>/v1/enrol` with `{org, tokenId, deviceId, fingerprint, computerName, dnsHostName, nonce,
spki, record, signature, proof}` – the proof is the token's HMAC over the request (as the broker registration's proof
is today), `record` the device's self-signed record (§7.3). The answer, signed with the environment's key:

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

Refusals: 401 (proof), 403 with a reason (token expired, revoked, over its cap, tenant disabled), 426 (agent older
than `minAgent`), 429. Each is audited for the tenant and logged on the device (2401). **Rediscovery** every
`rediscoverHours` (24; 1–168), after three failed connections to a service in a row, on the broker's close code
`4301 Moved`, and at every service start – signed with the device's key (no token needed after enrolment), through the
relay for a device behind a jump host (§7.5).

### 6.3 The console's answer

After sign-in (§8.1) the console calls `GET https://<environment>/v1/console/me` with its access token and its
console key's signature, and receives its **memberships**: for each tenant the role, the region, the broker and
monitoring addresses, the tenant's name and entitlements. It rediscovers on the same schedule as a device, and at
every token refresh.

## 7. Devices

### 7.1 The enrolment token

- **Form:** `rhe1.<org>.<token id>.<key>` – the shape of today's device secret with a new prefix, so an `rhd1` secret
  is still recognised (and treated as a legacy device secret, §9) while a token is never mistaken for one. The key is
  32 random bytes made in the console; the environment keeps its HMAC key derivation (the enroll key) and the
  lifecycle, never the key itself.
- **Lifecycle:** a name (*Intune rollout Q4*), an expiry (default 90 days; *never* allowed and shown in amber), a use
  cap (default none), the install defaults the console attached (§7.4), who created it, every use (time, device id,
  source address, agent version). **Revoke** stops new enrolments at once; enrolled devices keep working – their
  identity is their own key, not the token.
- **Created in the console** by an *Admin* or *Owner* (§8.2): *Settings → Environment → <tenant> → Enrolment tokens →
  New…*. Shown once, copied to the clipboard for 2 minutes as the device secret is today, or written into a `.rhagent`
  file or a token file for `ENROLLMENT_TOKEN_FILE`. The list shows uses and last use.

### 7.2 `enroll`

`RemoteHubAgent.exe enroll` (the MSI's deferred custom action, as today) keeps the steps of
[agent-spec.md](agent-spec.md) §5.3 and gains the environment:

1. Delayed start; the device's identity (device id, RSA-3072 CNG key, certificate) as today.
2. **Environment:** read the environment and the token – from the `.rhagent` file (format 3, §7.4), from the MSI
   properties `ENVIRONMENT` and `ENROLLMENT_TOKEN_FILE` (or `ENROLLMENT_TOKEN`, hidden), or from the command line with
   the token on standard input. Fetch the discovery document, send the proof with the self-signed record (§6.2),
   write the answer into `agent.json` (§7.4). The token is zeroed and never kept; the token file is deleted as the
   `.rhagent` file is.
3. Firewall rule and listener as today (`ListenEnabled`, the scope from the file).
4. Virtual display and automatic sign-in as chosen.

A failure at step 2 fails the install with the environment's reason and rollback runs `cleanup` as today. Without an
environment and without a format-2 file, a first install fails saying what to give it.

### 7.3 The device's record

What consoles trust about a device, **signed by the device's own key** (PS256 over the canonical JSON, as
`BrokerProtocol.SignPs256`):

```json
{ "deviceId": "…", "org": "o_…", "fingerprint": "<SHA-256 of the certificate>", "computerName": "…", "dnsHostName": "…",
  "label": "…", "addresses": [ … ], "upstream": "<jump host's device id or absent>", "agentVersion": "…", "signed": "…" }
```

The environment stores it with the device's public key and lists it for the tenant; a console verifies the signature
against the key the environment lists, and pins the fingerprint. A record that doesn't verify, or a device whose key
changed, is shown as *not verified* and never connected to with credentials – exactly what *not sealed* means today.
The device re-signs its record when its addresses, name or agent version change (the broker connection carries it,
as the registration does today).

### 7.4 `agent.json` and the enrolment file

The `broker` object becomes `environment`:

```json
"environment": {
  "url": "https://staging.remotehub.dev", "org": "o_…", "tokenId": "t_…",
  "region": "eu",
  "broker": { "url": "https://eu.broker.staging.remotehub.dev", "stun": "…" },
  "monitoring": { "url": "https://eu.monitoring.staging.remotehub.dev" },
  "updates": { "url": "…" },
  "keys": [ { "kid": "2026-11", "spki": "…" } ],
  "upstream": { "deviceId": "…", "host": "jump01.contoso.com", "port": 58732, "fingerprint": "…" },
  "discoveredUtc": "…", "rediscoverHours": 24
}
```

No enroll key and no record key any more: the device's own key is its identity (§4). An `agent.json` with the old
`broker` object is migrated (§9). `RemoteHubAgent.exe environment status | rediscover | leave` replace
`broker status | leave`.

The enrolment file, **format 3**: `AgentEnrollment` keeps its install settings (label, port, allowed groups, firewall
scope, virtual display, the monitoring defaults of [device-monitoring-design.md](device-monitoring-design.md) §11.3)
and replaces the console's `keyId` and `secret` with `environment`, `token` and an optional `upstream`. The console
writes it from *Install RemoteHub Agent* and from *Settings → Environment → Save enrolment file…*. A format-2 file is
still read by the agent and claimed by the console for the legacy path; format 1 stays refused.

### 7.5 Jump hosts: the environment relay

A device on a network without a path to the environment **has no broker connection**. Consoles reach it as today,
through the jump host's tunnel, by its place in the tree ([jump-host-design.md](jump-host-design.md)). The
environment gives it an identity in the tenant and a way out for its monitoring data, through the jump host's agent:

- The console, which knows the chain from the tree, gives the device an **upstream** with its enrolment (the jump
  host's device id, address, port and certificate fingerprint; in the format-3 file or the MSI property `UPSTREAM`).
  The device reaches the environment through `https://<upstream>:58732/v1/environment/forward/<host>/<path>` – the
  discovery document, the enrolment, monitoring uploads, rediscovery and the poll below – checking the upstream's pin
  as the console does. It opens no WebSocket to the broker, through the relay or otherwise.
- Its **record** names the upstream, so consoles place it under the jump host in the tenant's folder and route to it
  through the tunnel. It has no online state of its own; the console shows *reached through JUMP01* and the jump
  host's state.
- **A wake channel through the relay.** The device holds one long-lived WebSocket through the relay to the
  environment's **wake endpoint** on the monitoring front door (`/v1/o/{org}/devices/{id}/wake` – not the broker, which
  no relayed device talks to), signed with its key, so an activation or new settings reach it at once, as a broker
  wake reaches a connected device. One connection per relayed device on the jump host, within the relay's limits.
  Where the channel is down the device falls back to a **poll** every `MonitoringPollMinutes` (15; 5–1440) and with
  every upload ([device-monitoring-design.md](device-monitoring-design.md) §6.5).
- The jump host's agent forwards **only to the environment's discovered hosts** (the environment, its monitoring and
  update hosts – never the broker), byte for byte, TLS from the device to the environment inside the forward, so the
  jump host sees sizes and hosts, not content. Anything else – a `CONNECT`, a WebSocket to any path but the wake endpoint – is refused (2202). Limits: `EnvironmentRelay`, at most 16 concurrent forwards per source and 256 per jump host, sources within
  `TunnelTargets` when set, special addresses refused as for tunnels. Audit 1203 per source and hour.
- Chains work hop by hop; the console re-sends the upstream when a device is moved in the tree (the move's
  confirmation says so).

Until the relay is built (phase 3), a device behind a jump host stays on the legacy path.

## 8. Consoles

### 8.1 Sign-in

- *Settings → Environment → Add…*: the environment's name, then **Sign in**. The console opens the environment's
  OpenID Connect authorisation in the system browser (authorisation code with PKCE, loopback redirect) – or, where no
  browser may be used, the **device-code** flow with a code to type on another device. The environment's own
  authorisation server federates to the tenant's identity provider: **Entra ID** first (the device management world
  lives there), any OpenID Connect provider by configuration, and **RemoteHub accounts** (e-mail and a passkey) for
  tenants without one. Which provider a person uses follows from their e-mail domain, as Entra ID does it.
- The environment returns an access token (1 hour) and a refresh token (the identity provider's policy, at most 90
  days, revoked when the person's account is disabled or their membership removed), kept in the console's encrypted
  database. The console registers its **key pair** (the one it has today) on first sign-in on this installation; the
  environment lists a person's consoles, and the person or an *Owner* can remove one.
- A tenant in which the person has no membership isn't shown. A person with memberships in several tenants sees all
  of them; a person in several environments adds each.
- **Re-authentication** when the identity provider asks (conditional access, MFA every n days): the console shows the
  tenant's folder greyed with *Sign in again* and keeps working on everything else.

### 8.2 Members and roles

| Role | May |
|---|---|
| **Owner** | Everything an Admin may, plus manage members and roles, the tenant's identity provider and region, and delete the tenant |
| **Admin** | Activate monitoring, create and revoke enrolment tokens, change the tenant's folders, install and update agents, change monitoring settings, see the audit |
| **Operator** | Connect to devices, use every remote tool the device's Windows account allows, see monitoring |
| **Viewer** | See devices, folders and monitoring; no sessions |

Members are added by an *Owner* with an e-mail address (an invitation the person accepts by signing in), or by a
group of the tenant's identity provider mapped to a role (*Entra ID group → Operator*), so joiners and leavers follow
the directory. The environment roles decide what the environment and the console offer, and – where the device's
policy maps them – what a person may do on a device (§8.5).

### 8.3 What the console gets

From `GET /v1/console/me` and the tenant's endpoints: the tenants and roles, each tenant's region and service
addresses, its folders and placement (the shared layout of today's synced folders, now signed by the environment
rather than sealed with a layout key), its devices with their self-signed records, the entitlements (monitoring and
its tier), the enrolment tokens (Admin and above), the members (Owner), and the tenant's audit. The tenant's folder in
the navigation works as the synced folder does today; what the console adds of its own (credentials, RDP options,
notes, colours) stays its own.

### 8.4 Sessions: the attestation

A console that wants a session through the broker asks the environment for an **attestation** for that device:
`POST /v1/o/{org}/attest {deviceId, consoleKeyHash, nonce}` with its access token; the environment answers with a
short-lived statement signed with its key – `{org, deviceId, consoleKeyHash, user: "j.doe@contoso.com", userId,
role, issued, expires (10 minutes), nonce}`. The session request to the device (through the broker, or directly – the
same attestation serves a direct connection when the device has an environment) carries the attestation and the
console's signature over it. The device:

1. verifies the environment's signature with the keys it has from discovery (§6.1),
2. checks the attestation names this device and the console key that signed the request, and hasn't expired,
3. applies policy – `BrokerUsers` (UPNs or Entra ID object ids allowed) and `BrokerRoles` (the least role allowed)
   replace `BrokerConsoles`,
4. then either signs the person in by role (§8.5) or asks for Windows credentials as it does for every session
   today – the device's `Rights` policy decides which.

The audit on the device (1301 and the session events) records the person and the console's id. A console without an
attestation (an older console, a person whose membership ended an hour ago) is refused with 2400 and the reason.

### 8.5 Roles as agent rights (RBAC)

The agent's rights – `PowerShell`, `Desktop`, `DesktopViewOnly`, `SecureDesktop`, `ConsentBypass`, `PrivacyScreen`,
`Tunnel`, `AutoSignIn`, and monitoring's `Monitoring` and `MonitoringSettings` – are granted today to Windows groups
of the device (`Rights\<right>`, [agent-spec.md](agent-spec.md) §4.2). With the environment they can also be granted
to **roles**: an entry `role:Operator` (or `role:Admin`, `role:Owner`, `role:Viewer`) in a right's list means an
attested person with that role has it, with or without a Windows account on the device.

- **Defaults for a device enrolled in an environment:** `PowerShell`, `Desktop`, `SecureDesktop`, `PrivacyScreen`
  and `Tunnel` to `role:Operator` and up; `DesktopViewOnly` and `Monitoring` to `role:Viewer` and up;
  `MonitoringSettings` to `role:Admin` and up; `ConsentBypass` and `AutoSignIn` to nobody, as today. The
  `allowedGroups` of the enrolment file stay in every right's list beside the roles, so a local administrator still
  signs in with credentials where that is wanted. A tenant that wants Windows credentials only sets the policy
  `AttestedSessions` = 0 (§11), and the roles are then a gate as in §8.4 and nothing more.
- **Who the session runs as:** an attested session without Windows credentials runs its PowerShell and its desktop
  helper under the agent's **LocalSystem** token – the context the device actions and *System* script runs already use
  – and never impersonates a signed-in user (the *User* and *As me* run contexts of the scripts design still need
  credentials). Files, registry, services, processes and the desktop therefore act as the device itself, as an
  administrator's session does today. UAC doesn't apply.
- **Audit on the device:** every session, refusal and action records the person (UPN and object id), the role and
  the console id from the attestation, in the log file and the Application log (1000, 1100, 1200 and their kin), so
  Intune or a SIEM can tell *who* acted without a Windows account to look up.
- **What the person can still not do:** anything the device's policy forbids (`PowerShellEnabled`, `DesktopEnabled`,
  the consent modes, view-only) – policy is checked after the right, as today – and anything outside the attestation's
  ten minutes without a fresh one for the next session.
- **The exposure, stated:** a role in the environment is now administrative access to every device of the tenant
  whose `Rights` accept the role. Multi-factor sign-in at the identity provider, the short attestation, the
  console-key binding of the refresh token and the device's own `Rights`, `BrokerUsers` and `AttestedSessions` are
  the controls, and the README says it plainly. A compromised environment could add an operator (§4) – the audit on
  every device names them.

### 8.6 Tenants, trials and licences

A tenant comes into being in one of two ways, and carries **entitlements** the console and the services read:

- **Self-service:** a person signs up at the environment (`https://<environment>/signup`: e-mail, the identity
  provider or a RemoteHub account, company, region EU or US, and **payment details**, verified but not charged), and
  gets a tenant as its *Owner* with a **14-day trial** of everything for **10 devices**: agents, monitoring at the
  90-day tier, the broker. The eleventh device's enrolment is refused with the reason (`trial cap`), and the console
  says so where it offers the install; sales can raise the cap for a tenant. During the trial the console shows the days left; at its end the tenant falls to what was
  bought, and devices beyond the bought licences stop being served (sessions refused with the reason, monitoring
  uploads refused with 403 `unlicensed`) until the Owner buys more – nothing is deleted for 30 days.
- **Sales:** RemoteHub creates a tenant for an enterprise customer with its first Owner's e-mail, assigns licences and
  terms, and may extend a trial; the same records, set through the environment's admin API.
- **Licences** are counted per **device** for the agent (a device that connected in the last 30 days counts) and per
  **feature tier** for monitoring (30, 60 or 90 days, per device), bought in the console (*Settings → Environment →
  <tenant> → Licences*, which opens the environment's billing page in the browser) by an Owner, monthly or yearly.
  An Admin sees the counts; an Owner buys. Over-use is tolerated for 14 days with a banner before it is enforced.
- **Entitlement record** (D1): `{org, trialEndsUtc, agentDevices, monitoringTier, monitoringDevices, region, terms,
  gracePeriodEndsUtc, billingCustomerId}` – read by the console with the memberships (§6.3), by the broker on every
  session, by the monitoring front door on every upload and activation. The environment's audit records every change.
- **Payment:** Stripe – hosted checkout and the customer portal in the browser, a **card authorisation at sign-up**
  (verified, not charged), subscriptions monthly or yearly; sales-created tenants are **invoiced** through the same
  provider. The environment stores the customer id and the subscription state, never card data; webhooks from the
  provider update the entitlement record. Invoices, tax and refunds are the commerce design's; this document fixes
  the provider, the authorisation at sign-up, the 14-day trial and the entitlement record.

### 8.7 The tenant key

For a tenant that can't accept that the environment alone may add a device or a person (§4), a key the tenant holds
must **co-sign** every admission before consoles and devices trust it. Off by default; an Owner turns it on in
*Settings → Environment → <tenant> → Security* and the console says what it costs (below).

- **The key:** an ES256 key pair made in the Owner's console when the setting is turned on. The private key lives in
  that console's encrypted database ([local-db-design.md](local-db-design.md) §4) and in the databases of the other
  **holders**; the public key is published for the tenant by the environment and pinned by every device at its next
  rediscovery (and in the enrolment file for new devices) and by every console at sign-in.
- **Holders:** Owners and, if the Owner chooses, Admins. A new holder's console gets the key **wrapped for its console
  key** by a holder's console that is signed in (a linking step the new console shows as *waiting for an Owner to
  approve this console*; the approving console shows who and which console). A **recovery phrase** (24 words, shown
  once when the key is made) restores the key into a console with no holder left.
- **What is co-signed:** a device admission `{org, deviceId, spki, admitted}` and a membership `{org, userId, upn,
  role, since}`, each by the tenant key over the canonical JSON. The environment stores the co-signatures with the
  records and delivers them with the device list and inside attestations.
- **What checks them:** a console trusts a device's record only with a valid admission co-signature; a device accepts
  an attestation only when the membership it names is co-signed (the attestation carries it). Policy `TenantKey`
  (§11) lets IT require the co-signature on devices regardless of the tenant's setting.
- **How admissions happen:** a holder's console co-signs **on sight** – each new device and each new member (from an
  invitation or an identity-provider group) appears as *pending* in the tenant's folder and under *Members* with
  *Admit*; an Owner can set *admit devices enrolled with a token I created automatically* so an Intune rollout doesn't
  wait for clicks, and *admit members from directory groups automatically while a holder's console is signed in*. A
  pending device is reachable by nobody; a pending member can see nothing.
- **What it costs:** a holder's console must be signed in for anything to be admitted; losing every holder and the
  recovery phrase makes a new tenant key necessary, which re-admits every device and member (a guided step). The
  environment's own operations (support access, entitlements) don't need it.
- **What it closes:** the environment can list a device or a member, but no console or device trusts them until a
  holder co-signs; it can't sign a membership or an admission, since it never holds the key. The device-signed record
  still prevents an existing device from being replaced. What remains is denial of service, as in §6.2.

## 9. Legacy and migration

| Who | After the update | Later |
|---|---|---|
| A broker organisation with device secrets and console secrets | The environment that now runs the broker turns each organisation into a **tenant**: the people who hold its console secrets are invited as *Owners* at their first sign-in from a console that still holds the secret (the console proves it once, then drops it). Devices keep their registrations; an updated agent signs its record at its next connection. **Seals and the keyring go with phase 2:** a device whose agent hasn't been updated to sign its record shows as *not verified* until it is, and the console offers *Update RemoteHub Agent* on it – the update runs through the agent as today | – |
| A device joined with `BROKER_URL` + device secret | The agent treats the broker as its environment, discovers at `https://<broker>/.well-known/remotehub`, fills in `environment`, drops the derived keys once its signed record is accepted | – |
| A console-enrolled device without a broker (format-2 file, claim) | Unchanged until phase 2; marked *legacy* in the console; not monitorable | *Join environment…* moves it; the claim and the console's enrolment secret stay readable for devices already enrolled |
| A new install | Phase 1: an environment and a token, or a format-2 file (logged as legacy). **Phase 2: format 2 is refused for new installs** with a message naming the environment | – |

Policies keep their meaning: `BrokerUrl` is read as an allowed environment as well; `BrokerConsoles` stops mattering
with phase 2 and is listed as retired in the template; the `RdpManager` rule applies.

## 10. Broker protocol v2

What changes in `BrokerProtocol.cs` and on the broker, as a list for the broker's repository:

- **Records are signed by the device** (PS256 with the device certificate's key) instead of sealed with the record
  key; the environment stores and lists the public key with the record. `KeyLabel.Record`, `Layout` and `Keyring` go;
  `Enroll` stays for the token's proof.
- **Console registration by sign-in:** a console registers its key with an access token, not a console secret; the
  keyring blob goes.
- **Session offers carry an attestation** (§8.4) signed by the environment, and the console's signature over it;
  the device checks both and its `BrokerUsers`/`BrokerRoles` policy.
- **Layouts** (the shared folder structure) are signed by the environment on write by an Admin, instead of sealed
  with the layout key.
- **New endpoints:** `/.well-known/remotehub`, `/v1/enrol`, `/v1/auth/*` (OpenID Connect), `/v1/console/me`,
  `/v1/o/{org}/attest`, `/v1/o/{org}/tokens`, `/v1/o/{org}/members`, `/v1/o/{org}/audit`, `/v1/o/{org}/tenantkey`
  (the public key, pending admissions, co-signatures, console linking); the monitoring front door's
  of [device-monitoring-design.md](device-monitoring-design.md) §9.
- **Attested sessions carry the role** the device maps onto its rights (§8.5); the session start names the person
  and role in the device's audit.
- **A wake endpoint** on the monitoring front door for relayed devices (§7.5).
- **Close codes:** `4301 Moved` (rediscover), `4302 NotAttested`, `4303 Unlicensed` (§8.6).
- The test vectors in `BrokerProtocolTests` grow accordingly; v1 vectors stay while legacy records are read.

## 11. Policy

Agent (`HKLM\SOFTWARE\Policies\RemoteHub\Agent`, ADMX category *RemoteHub Agent → Environment*):

| Value | Type | Meaning (default) |
|---|---|---|
| `Environment` | SZ | The only environment the device may enrol in and rediscover from (any) |
| `EnvironmentKeys` | MULTI_SZ | Key ids or SPKI hashes the environment's signatures must come from – a pin for organisations that want one (the keys discovery delivered) |
| `EnvironmentRelay` | DWORD | 0: this device never relays other devices' environment traffic (§7.5) (1 when `TunnelEnabled` is on) |
| `BrokerUsers` | MULTI_SZ | UPNs or identity-provider object ids whose attestations are accepted (every member of the tenant) |
| `BrokerRoles` | SZ | The least role an attestation must carry to reach the device at all: `Viewer`, `Operator`, `Admin`, `Owner` (`Viewer`) |
| `AttestedSessions` | DWORD | 0: an attested person must still sign in with Windows credentials – roles in `Rights` are ignored, the roles are a gate only (1) |
| `TenantKey` | DWORD | 1: accept only attestations whose membership carries the tenant key's co-signature (§8.7), whatever the tenant's setting (0) |
| `Rights\<right>` | MULTI_SZ | As today, plus `role:Viewer`, `role:Operator`, `role:Admin`, `role:Owner` entries (§8.5); the enrolment's defaults as listed there |
| `BrokerConsoles` | MULTI_SZ | Legacy consoles' ids, as today, until the seals go |
| `BrokerEnabled`, `BrokerUrl`, `BrokerHttpProxy`, `BrokerKeepAliveSeconds`, `BrokerDirect`, `BrokerStun`, `ListenEnabled`, `TunnelOwnPorts` | as today | `BrokerUrl` doubles as an allowed environment |

Console (`SOFTWARE\Policies\RemoteHub`): `AllowEnvironments` (MULTI_SZ, the environments this PC may sign in to; any),
`EnvironmentSignInAccounts` (MULTI_SZ, e-mail domains the person may sign in with; any) beside `AllowBroker`.

## 12. Security

- **The new exposure, stated:** the environment can add a device or a person to a tenant (§4). It can't change an
  existing device's pin, speak as a device, read a session or sign in to one. Mitigations: new devices land in *New
  devices* and get no credentials until used; a registration without a token use is in the tenant's audit; members
  added outside the directory mapping are in it too; the tenant key (§8.7) closes the exposure for tenants that turn it on.
- **A role is administrative access** to the tenant's devices whose `Rights` accept it (§8.5): the controls are MFA at the
  identity provider, the 10-minute attestation bound to the console key, `BrokerUsers`, `AttestedSessions` and the
  device's audit naming the person. The README says so where it describes roles.
- **Trial and licence enforcement** happens at the broker and the monitoring front door with a 14-day grace and a
  30-day retention after the trial, so a lapse never deletes data at once (§8.6).
- **A stolen token** enrols rogue devices until it expires, hits its cap or is revoked – visible, bounded, and no
  existing device's identity can be taken (SEC-ENR-03 holds: every device makes its own key).
- **A stolen refresh token** acts as the person until the identity provider revokes it or the person's account is
  disabled; it is stored in the console's encrypted database under the same key hierarchy as passwords
  ([local-db-design.md](local-db-design.md) §4), bound to the console's key (the environment accepts it only with that
  key's signature), and the person can remove the console from their list.
- **A compromised console key** without the person's token can't sign in; a token without the key can't be used.
- **Discovery spoofing:** TLS first, the environment's signatures second, `Environment` and `EnvironmentKeys` third.
  A spoofed answer can deny service, not open a session.
- **On the device:** the token from a file or standard input, never a command line (`ENROLLMENT_TOKEN` exists for
  tools that can't write a file and is hidden in the MSI log as `BROKER_SECRET` is); zeroed after the proof. Lab case
  E-ENR-04 checks nothing is left.
- **The relay is not a proxy:** fixed destination set from the jump host's own discovery, never the broker, no
  `CONNECT`, no WebSocket, limits per source (SEC-ENV-02).
- **Threat boundaries** TB2/TB3 (identity, enrolment) in [security-test-lab-design.md](security-test-lab-design.md)
  get the token, the sign-in and the attestation; the technology inventory gets the environment under §2.

## 13. Audit

Agent: 1304 `EnvironmentEnrolled` (environment, tenant, region, token id), 1305 `EnvironmentRediscovered` (what
moved), 1203 `EnvironmentForwarded` per source and hour, 2401 `EnrolmentRefused`, 2202 `EnvironmentForwardRefused`,
3401 `EnvironmentError`; the session events (1301, 1100, 1000 …) gain the person's UPN and role from the attestation,
2400 names a missing or refused attestation. Environment, per tenant: token created, revoked, used; member invited,
joined, removed, role changed; console registered and removed; sign-in refused; attestation refused; device registered
(with or without a token use). Shown under *Settings → Environment → Audit*.

## 14. Tests

Unit (`tests/RemoteHub.Tests`): the discovery document and answers parsed within their limits and refused outside
them; a wrong signature refused, an unknown key logged; `enrol` from the MSI properties, the format-3 file and standard
input; a token on the command line refused unless `ENROLLMENT_TOKEN`; format 2 still read, format 1 refused; the old
`broker` object migrated; the `Environment` policy refusing another; the relay's destination set and limits; the
device's record signed and verified, a changed field or key refused; the attestation verified, expired, for another
device or console refused, `BrokerUsers`/`BrokerRoles` applied; the sign-in flows against a test authorisation server
(PKCE, device code, refresh, revoked refresh token); the refresh token stored under the database key and bound to the
console key.

Lab (`docs/test-lab-cases.md` *Agent – environment (E)*; the lab's broker gains discovery, enrol, a test identity
provider and the console endpoints, test-lab-spec §5.3):

| ID | Case | Pass when | How | P |
|---|---|---|---|---|
| E-ENR-01 | MSI with the environment | `ENVIRONMENT` + `ENROLLMENT_TOKEN_FILE` → installed, `agent.json` has `environment` with broker and monitoring from discovery, the signed record registered and online at the lab broker, 1304 and 1300 audited, token file gone | PS E | 1 |
| E-ENR-02 | Format-3 file | the console's *Save enrolment file…* with a token → the MSI next to it enrols the same way; the device appears in the tenant's folder with a verified record and its pin | H PS | 1 |
| E-ENR-03 | Token refused | expired, revoked, over its cap, another tenant's → the install fails with the environment's reason, rollback clean, 2401 audited; the tenant's audit lists the attempt | PS H | 1 |
| E-ENR-04 | Nothing left | no token in `agent.json`, the log, the MSI log, process-creation events (4688 with command-line auditing on) or temp folders | PS E | 1 |
| E-DISC-01 | Rediscovery | the lab environment changes the tenant's broker URL → the agent reconnects to the new one within `rediscoverHours` set to 1, 1305 audited; the broker's `4301 Moved` makes it immediate | H PS | 1 |
| E-DISC-02 | Signature and policy | a document with a bad signature refused; `Environment` set to another name → enrol refused; `EnvironmentKeys` pinned to a wrong key → refused and logged | PS | 1 |
| E-SIGN-01 | Console sign-in | **2VM**: *Settings → Environment → Add* with the lab environment, sign in as a test person (PKCE in the browser, and the device-code flow) → the tenants with roles, the folder, devices with verified records; a token created and used by E-ENR-01's install | U H | 1 |
| E-SIGN-02 | Roles | a *Viewer* sees devices and gets no session (the attestation is refused with the role, 2400 on the device); an *Operator* connects; an *Admin* creates a token; the person's membership removed → the folder greys within an hour, sessions refused at once | U H | 1 |
| E-SIGN-03 | Attestation on the device | a session request without an attestation, with an expired one, one for another device, one signed by another key → refused (2400, the reason named); a valid one → the Windows sign-in follows as today; `BrokerUsers` excluding the person → refused; the device audit names the UPN | H | 1 |
| E-RBAC-01 | Roles as rights | an *Operator* with no account on the device opens PowerShell and the desktop through the broker without credentials; the session runs as SYSTEM; the device audit names the UPN and role; a *Viewer* gets view-only and no PowerShell; `AttestedSessions` = 0 → credentials asked again; `Rights\PowerShell` without `role:Operator` → 403 | H PS E | 1 |
| E-TRIAL-01 | Trial and licences | a self-service tenant at the lab environment: 14 days of everything for 10 devices, the console shows the days and the count; an eleventh enrolment is refused with `trial cap` and 2401 on the device; the trial expired with no licence → sessions and uploads refused with `unlicensed`, nothing deleted; a licence assigned through the admin API → served again within a minute | H U | 1 |
| E-KEY-01 | Tenant key | an Owner turns it on → a device enrolled with a token is *pending* and unreachable until *Admit*; a member from a directory group is pending and sees nothing; auto-admit for the Owner's tokens admits E-ENR-01's device without a click; a second console becomes a holder only after the first approves it; the recovery phrase restores the key into a fresh console | U H | 1 |
| SEC-ENV-04 | The environment alone | the lab environment's admin API adds a device and a member to a tenant with the key on → both pending, no console trusts the device's record, the member's attestation is refused by a device with `TenantKey` = 1 and by one with the tenant setting (2400 names the missing co-signature) | H | 1 |
| E-LEG-01 | Legacy | a device joined with `BROKER_URL` + device secret and a console with the console secret before the update → the organisation is a tenant, the console's person is *Owner* after the first sign-in, the device's signed record replaces the sealed one at its next connection, same device id and pin | H PS | 1 |
| E-RELAY-01 | Through a jump host | **2VM**, the target without a path: `UPSTREAM` in the file → enrolled through the relay, its record names the jump host, it appears under the jump host and is reached through the tunnel; no broker connection (no 1300); monitoring uploads go through the relay, the wake channel is up and an activation reaches it within seconds, the poll takes over when the channel is cut; 1203 on the jump host; its log has no content | H N | 1 |
| SEC-ENV-01 | Rogue device | a token used from the attacker VM → the device lands in *New devices* with a record of its own, no console connects to it with credentials, the cap and revoke stop further uses, the audit shows the source; a record claiming an existing device's id with another key → *not verified*, never connected to | H | 1 |
| SEC-ENV-02 | Relay is not a proxy | forwards to a host outside the jump host's discovered set (the broker included), a `CONNECT`, a WebSocket to any path but the wake endpoint, a body over the limit, a 17th concurrent forward → refused (2202), nothing sent | H N | 1 |
| SEC-ENV-03 | Stolen tokens | a copied refresh token without the console's key → refused; a console key without a token → no access; a refresh token after the person is disabled at the test identity provider → refused within the access token's hour | H U | 1 |

## 15. Docs in step

[agent-spec.md](agent-spec.md) §4.1 (consoles attested, the person in the audit), §5 (enrolment rewritten), §7
(`environment`, the policies), §8, §11; README *RemoteHub Agent* and *RemoteHub Broker* → *Environment* (sign-in,
roles, what the environment holds and can do – §4 said plainly); `PRIVACY.md` (the person's account, what the
environment stores); [admx-design.md](admx-design.md) §5 and §9; [jump-host-design.md](jump-host-design.md) §6 (the
relay); [threat-model-technologies.md](threat-model-technologies.md) §2 and §3 (the trust model of §4);
[security-test-lab-design.md](security-test-lab-design.md) (TB2/TB3, SEC-ENV); [local-db-design.md](local-db-design.md)
§6 (the refresh token and the console key); `test-lab-spec.md` §5.3; `installer/…/Package.wxs`;
[device-monitoring-design.md](device-monitoring-design.md) §9.4 and §14 (the console's access token in place of the
console key for queries).

## 16. Phases

| Phase | Content |
|---|---|
| **1 – Devices and the environment** | Discovery, `/v1/enrol`, signed records, the lab environment; `enroll` with `ENVIRONMENT`/`ENROLLMENT_TOKEN_FILE`, format 3, `agent.json` `environment`, rediscovery, migration of `broker`; policies `Environment`, `EnvironmentKeys`; audit 1304/1305/2401/3401. Consoles still join with the console secret during this phase |
| **2 – People** | The authorisation server with Entra ID and RemoteHub accounts (generic OpenID Connect in the release after); members and roles; `/v1/console/me`; attestations and the device's check (`BrokerUsers`, `BrokerRoles`); **roles as agent rights** (`role:` entries in `Rights`, `AttestedSessions`, SYSTEM sessions with the person in the audit, §8.5); **sign-up, the 14-day trial and entitlements** (§8.6; Stripe checkout and portal, webhooks into the entitlement record); the **tenant key** (§8.7: key, holders and linking, admissions, co-signatures in attestations, `TenantKey`); *Settings → Environment* with sign-in, tenants, tokens, members, licences and the audit; environment-signed layouts; the migration of organisations and console secrets (§9); **format 2 refused for new installs, seals and the keyring removed**; `AllowEnvironments`, `EnvironmentSignInAccounts` |
| **3 – The relay** | `/v1/environment/forward`, upstreams from the tree, records naming the upstream and the console's placement under it, the wake channel through the relay, `EnvironmentRelay`, 1203/2202; monitoring's relay folds into it |
| Later | Agent updates from the environment's feed; the commerce design (invoices, tax, refunds); a customer-hosted environment; removing the claim and the console's enrolment secret once no legacy device is left |

Monitoring's phase 1 ([device-monitoring-design.md](device-monitoring-design.md) §17) needs phase 1 here; its
console side needs phase 2 (the access token for queries).

## 17. Open questions

Every question put to the owner is decided (§18). The judgement calls below were made without asking and stand until
someone objects:

| # | Judgement call | Where |
|---|---|---|
| J1 | Four roles – Owner, Admin, Operator, Viewer – and what each may do | §8.2 |
| J2 | The rights an Operator gets by default on an environment-enrolled device (SecureDesktop included – confirmed) | §8.5 |
| J3 | Trial: 14 days of tolerated over-use after a licence lapse; 30 days of retention after it | §8.6 |
| J4 | Refresh tokens live at most 90 days; access tokens an hour | §8.1 |
| J5 | Rediscovery every 24 hours; the relay's limits (16 forwards per source, 256 per jump host) | §6.2, §7.5 |
| J6 | The tenant key is ES256 with a 24-word recovery phrase; holders are Owners and, optionally, Admins | §8.7 |

## 18. Decided

| Decision | Settled by |
|---|---|
| Consoles sign in as people; no console secret, no joining a broker | The owner (v2): the environment name and user authentication to the tenant are enough |
| The environment is trusted for membership; devices sign their own records so an existing device can't be impersonated; Windows sign-in stays the authority on the device | The consequence of signing people in, kept as narrow as possible (§4) |
| Sessions carry an environment attestation of the person and role; the device checks it before the Windows sign-in | Puts the person into the device's audit and policy without changing what the Windows account decides |
| Roles Owner, Admin, Operator, Viewer; members by invitation or identity-provider groups | Judgement: the smallest set that separates managing a tenant, operating devices and looking |
| The broker is mandatory for new devices that can reach the environment; a device behind a jump host has none and is reached through the jump host | The owner: the broker is the main device connection point, not the only one |
| Environment URL + enrolment token for devices; discovery for every address | The owner |
| The token is `rhe1`, with a lifecycle at the environment | §17 Q5; no key material shared with consoles any more |
| Legacy organisations are migrated by the environment, not reinstalled; console-enrolled devices stay legacy | Principle 7 |
| The jump-host relay forwards to the environment's discovered hosts only, never the broker; relayed devices hold a wake channel to the monitoring front door through it and poll as the fallback | Not a proxy; the owner (v3): no broker connection behind a jump host, a wake in phase 3 |
| Roles grant agent rights in phase 2: `role:` entries in `Rights`, attested sessions run as SYSTEM with the person in the audit, `AttestedSessions` = 0 keeps credentials mandatory | The owner (v3) |
| Tenants: self-service sign-up with payment details and a 14-day trial of everything; licences per device and per monitoring tier bought by an Owner; sales creates and assigns too | The owner (v3): as Azure does it |
| Format 2 refused for new installs, seals and the keyring removed, with phase 2 | The owner (v3) |
| The listener stays on by default for new installs, scoped by the file's firewall addresses | The owner (v3) |
| Entra ID and RemoteHub accounts in phase 2, generic OpenID Connect in the release after | The owner (v3) |
| The token is `rhe1` | The owner (v3) |
| The tenant key is designed and built in phase 2, off by default, turned on per tenant | The owner (v4): with roles as rights, a tenant must be able to keep the environment from adding operators on its own |
| Stripe, with a card authorisation at sign-up and invoices for sales-created tenants | The owner (v4) |
| An attestation is valid for 10 minutes to reach the device; the session then lives by the device's rules | The owner (v4) |
| Operator gets SecureDesktop by default; the device policy can take it away | The owner (v4) |
| The trial is capped at 10 devices; sales can raise it | The owner (v4) |
