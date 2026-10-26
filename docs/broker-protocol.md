# RemoteHub Broker – wire protocol

Version `remotehub-broker/1`. The contract between the RemoteHub Broker (a service of its own, not in this repository) and its clients here: the agent (`src/RemoteHub.Agent/BrokerProtocol.cs`, `Broker/`) and RemoteHub (`src/RemoteHub/Broker/`). Both sides check the test vectors of §10 (`tests/RemoteHub.Tests/BrokerProtocolTests.cs`). Nothing in it relies on being secret. The design notes the code cites as broker-design.md are kept with the broker.

## 1. Encodings

| Name | Meaning |
|---|---|
| b64u | base64url (RFC 4648 §5) **without padding**. Exactly one spelling per value: padding, other alphabets and set unused bits are refused. |
| b32 | RFC 4648 base32, **lower-case** alphabet `a–z2–7`, no padding. |
| hex | upper-case hexadecimal (`AB12…`). |
| time | integer Unix seconds. |
| text | UTF-8; invalid UTF-8 is refused, never replaced. |

## 2. Identifiers

| What | Form | Example |
|---|---|---|
| Organisation | `o_` + 16 b32 (80 random bits) | `o_k7q2xmd4tpa3fhwz` |
| Secret id | `s_` + 8 b32 (40 random bits) | `s_chdhdh7d` |
| Device, console, folder, target | lower-case GUID | `3f1a9e2c-7b44-4b2a-9e21-0c6a2f9b7e10` |
| Session | b64u of 16 random bytes (22 characters) | |
| Certificate fingerprint | hex SHA-256 of the DER certificate (as `AgentProtocol.Fingerprint`) | |
| Key hash | b64u SHA-256 of a public key's SPKI DER | |
| Short name | `a–z0–9-`, 3–32, starting and ending with a letter or digit | `contoso` |

## 3. Secrets, keys, proofs

### 3.1 Secrets

- Device secret: `rhd1.<org>.<secret id>.<b64u 32 bytes>`
- Console secret: `rhc1.<org>.<secret id>.<b64u 32 bytes>`
- `.rhbroker` file (what consoles import): `{"v":1,"broker":"https://rh.contoso.com","org":"o_…","name":"Contoso Ltd","consoleSecret":"rhc1…"}`

### 3.2 Derived keys

`HKDF-SHA256(IKM = the 32 secret bytes, salt = UTF-8 "remotehub-broker/1|<org>", info = UTF-8 <label>, L = 32)`

| Label | From | Held by |
|---|---|---|
| `enroll` | device secret | devices, consoles, **broker** |
| `record` | device secret | devices, consoles |
| `join` | console secret | consoles, **broker** |
| `layout` | console secret | consoles |
| `keyring` | console secret | consoles |

### 3.3 MACs

`mac(key, org, p1, p2, …) = b64u(HMAC-SHA256(key, UTF-8 "remotehub-broker/1|<org>|<p1>|<p2>|…"))`

| Use | Key | Parts |
|---|---|---|
| Device registration | enroll | `enroll`, device id, key hash, challenge nonce |
| Console registration | join | `join`, console id, key hash, challenge nonce |
| Relay | session key | `relay`, session id, `console` or `agent` |

### 3.4 Keyring

The broker keeps, per console secret, an opaque blob: `b64u(iv[12] ‖ AES-256-GCM(key = keyring, iv,
aad = UTF-8 "remotehub-broker/1|<org>|keyring|<console secret id>", plaintext))`, plaintext
`{"v":1,"deviceSecrets":["rhd1…", …]}` – every device secret of the organisation, retired ones included, so consoles can
check every device's seal.

### 3.5 Signatures and the proof text

- Consoles: **ES256** – ECDSA P-256, SHA-256, signature as IEEE P1363 `r‖s` (64 bytes; .NET's default format).
- Agents: **PS256** – RSASSA-PSS, SHA-256, MGF1-SHA-256, salt length 32, modulus ≥ 2048 (the agent's key is RSA 3072).
  ES256 is accepted for agents too.
- Public keys travel as b64u SPKI DER.
- Proof text: UTF-8 `remotehub-broker/1|<org>|<role>|<id>|<nonce>|<host>` – role `agent` or `console`; nonce and
  host exactly as in the challenge.

## 4. Seals

`seal(key, org, kind, body) = mac(key, org, "seal", kind, canonical(body))`

**Canonical JSON:** objects with keys sorted by UTF-16 code units (ordinal), no whitespace, `true`/`false`/`null`,
integers only (safe integers, no fractions or exponents), strings as ECMAScript `JSON.stringify` writes them: `"` and
`\` escaped, `\b \f \n \r \t`, other characters below U+0020 as `\u00xx` (lower-case hex), lone surrogates as `\udxxx`,
everything else (umlauts, emoji) as UTF-8 unescaped.

| Kind | Key | Body (all keys required, no others) |
|---|---|---|
| `device` | record | `{computerName, deviceId, domain, fingerprint, registered}` – `domain` may be `""`, `registered` a time set by the agent |
| `offer` | record | `{consoleId, consoleKey, deviceId, expires, kind, nonce}` – `consoleKey` the key hash, `kind` `device` or `support`, `nonce` b64u 16 bytes, `expires` ≤ now + 300 s |
| `wake` | record | `{consoleId, deviceId, expires, nonce}` (later version) |
| `announce` | record | `{jumpHost, targets: [{name, address, ports: [int]}], v}` – `jumpHost` the announcing device's id |
| `folder` | layout | `{id, name, parent (id or null), deleted, v}` |
| `place` | layout | `{id (device or target id), folder (id or null), label (text or null), v}` |
| `target` | layout | `{id, jumpHost, name, address, ports: {rdp?, winrm?, agent?, ssh?}, shares: [text], pins: {rdp (hex or null), ssh (text or null)}, forwards: [{name, port, browser}], deleted, v}` |

`v` is a version ≥ 1 that only goes up; the broker keeps the highest and answers `stale` to anything not higher.
The broker stores seals; it can check none of them except by shape.

## 5. Connections

| Path | Who |
|---|---|
| `GET /v1/health` | anyone: `{product, version, protocol}` |
| `wss://<host>/v1/o/<org>/console` | consoles |
| `wss://<host>/v1/o/<org>/agent/<device id>` | agents |
| `wss://<host>/v1/o/<org>/relay?session=<id>&side=console\|agent` | both sides of a relayed session, with `Authorization: RemoteHub-Relay <relay MAC>` |
| `https://<host>/v1/admin/…` | the operator (`Authorization: Bearer <admin token>`) – `rhbroker` |

An unknown organisation is answered with HTTP 404 before any object is reached.

### 5.1 Handshake (consoles and agents)

1. Broker → `{"t":"challenge","nonce":"<b64u 32 bytes>","host":"<host>"}`
2. Client → `proof` (registered) or `register` (first time, or again after the broker lost its data):

   ```
   {"t":"proof","id":"<id>","sig":"<proof signature>"}

   {"t":"register","id":"<console id>","publicKey":"<spki>","alg":"ES256","name":"Alice – laptop",
    "secretId":"s_…","join":"<join MAC>","sig":"<proof signature>"}

   {"t":"register","id":"<device id>","publicKey":"<spki>","alg":"PS256","certificate":"<b64u DER>",
    "record":{…device body…},"seal":"<device seal>","secretId":"s_…","enroll":"<enroll MAC>","sig":"<proof signature>"}
   ```
3. Broker → `{"t":"welcome","org":"o_…","orgName":"Contoso Ltd","consoleId":"…","cursor":<n>}` (consoles) or
   `{"t":"welcome","org":"o_…","orgName":"…","deviceId":"…"}` (agents) – or a close (§9).

Registration rules: the secret must be of the right kind and not retired; the record's id must equal the path's and
the message's; its fingerprint must be the certificate's SHA-256. A registered id with the **same** key registers again
without harm; with another key it is refused (4403). A removed device's id is refused (4403 – tombstone) until
`broker join` gives it a new id.

### 5.2 Keep-alive

The text message `ping` (not JSON), answered `pong` at the edge without waking the broker, every 30–90 s (60 by
default). A device without a ping for 2.5 intervals is closed (4408) and shown offline.

## 6. Agent ↔ broker

**Agent → broker** (after `welcome`), no others:

| Message | Fields |
|---|---|
| `hello` | `agentVersion?`, `os?`, `keepAlive?` (30–90), `macs?: [{mac "aa:bb:…", address, prefix}]` (≤ 16) |
| `rename` | `record` (only `computerName`, `domain` may change), `seal` |
| `accept` | `session`, `cands` |
| `refuse` | `session`, `reason` (letters, ≤ 32), `message?` (≤ 300) |
| `announce` | `body` (`announce`), `seal` |

**Broker → agent**, the closed list of design §7.1a – an agent closes the connection on anything else:

| Message | Fields |
|---|---|
| `challenge`, `welcome` | §5.1 |
| `incoming` | `session`, `key` (b64u 32), `kind`, `offer`, `offerSeal`, `source` (console's public address), `consoleName`, `cands`, `relay` (path) |
| `wake`, `checks` | later versions |
| `orgDeleted` | – (then close 4410) |
| `error` | `code`, `message` |

The agent checks an `incoming` offer before anything else: the seal with the record key, `expires` (± 120 s of clock
skew), a nonce not seen before, `deviceId` its own. It takes the console's id and key hash **from the offer**.

## 7. Console ↔ broker

Every request may carry `ref` (≤ 64 characters); the answer repeats it.

| Request | Answer |
|---|---|
| `sync {since}` | `changes {items, cursor, more}` – pages of ≤ 60 KB, ask again with `since = cursor` while `more` |
| `keyring` | `keyring {secretId, blob}` (blob `null` if none) |
| `write {item}` or `write {items}` (≤ 100) | `ack {cursor, results?}` – `item = {kind: folder\|place\|target, id, body, seal}`; one stale item: `error {code: "stale", v}`; in a batch, `results` per item `{kind, id, ok}` or `{kind, id, stale}` |
| `registerDevice {id, publicKey, alg, certificate, record, seal, secretId?}` | `ack {cursor}` or `error {code: "exists"}` – for enrollments the console made |
| `removeDevice {id}` | `ack` or `error {code: "unknown"}` |
| `consoles` | `consoles {items: [{id, name, registered, lastSeen, removed}]}` |
| `removeConsole {id}` | `ack` (removing oneself closes the connection, 4403) |
| `connect {deviceId, offer, offerSeal, cands}` | `accepted {session, key, cands, relay}` or `refused {reason, message}` |
| `wake` | `error {code: "unsupported"}` in this version |

**Pushed** to every connected console of the organisation: `changed {items, cursor}` and
`presence {deviceId, online, lastSeen}` (debounced 10 s).

**Items** in `changes` and `changed`:

```
{"kind":"device","id":"…","record":{…},"seal":"…","secretId":"s_…","os":"…","agentVersion":"…","online":true,"lastSeen":1793023426,"removed":false}
{"kind":"folder"|"place"|"target"|"announce","id":"…","body":{…},"seal":"…"}
```

`refused` reasons: `unknown` (no such device), `offline`, `timeout` (no answer in 10 s), `expired` (the offer's
time), `limit` (30 requests per minute, 200 open sessions per console), `unsupported`, or the agent's own reason.

A device item's `secretId` is the device secret it registered with (null for a device a console enrolled): the console
seals an offer with that secret's record key, which is the one the agent holds.

The broker refuses (closes 4400) an offer whose `deviceId`, `consoleId` or `consoleKey` don't match the request and the
connected console.

## 8. Relay

Both sides open the relay path with their MAC. The broker pairs them and copies **binary** messages between them
unchanged (≤ 64 KB each); a text message closes the connection. A side that arrives first may send up to 1 MB before
the other is there. 30 s after `accepted` without both sides the session ends. A second connection for a side is
refused (409), a wrong MAC 403.

Inside, both ends run the jump host's framing end to end (`TunnelProtocol` in RemoteHub: `[type][stream id][payload]`,
`Open` / `Opened` / `Refused` / `Data` / `Credit` / `Close` / `Reset`) with an **empty `Open` payload** – a stream always
ends at the agent's own HTTPS endpoint (design §5.1); each stream then carries TLS to the device's pinned certificate.

## 9. Limits and close codes

| Limit | Value |
|---|---|
| Message | 64 KB; sealed body 16 KB; names 200 characters |
| Candidates | ≤ 8, ≤ 64 characters: `host:192.0.2.10:51820`, `srflx:[2001:db8::7]:51820`, `relay:…` |
| Agent messages | 10 per second |
| Console messages | 50 per second |
| Connect requests | 30 per minute per console; 200 open sessions per console |

| Code | Meaning |
|---|---|
| 4400 | malformed or unknown message, wrong shape, too large |
| 4401 | proof or registration refused (wrong key, MAC or signature; retired secret) |
| 4403 | refused: removed, id taken by another key, device limit |
| 4404 | unknown console or device – an agent then registers again with its kept enroll key |
| 4408 | no keep-alive |
| 4409 | replaced by a newer connection of the same device |
| 4410 | the organisation was deleted |
| 4429 | too many messages |
| 1011 | broker fault |

## 10. Test vectors

Secret = bytes `00 01 02 … 1f`, organisation `o_aaaaaaaaaaaaaaaa` (`test/core.test.ts`):

### 10.1 Derived keys (b64u)

| Label | Key |
|---|---|
| enroll | `p-KvhM8ZobVAIpE3nPHc2iCzLGnoPpbsQ_Zgb7mVYWI` |
| record | `S_IQjOUjBaH0tXaNws0ygroPyXdhX2QzZ15Ntub9JVU` |
| join | `LqDxw3Vg-4ekDMi6UgRzJ1ucz0p5MvaSCkO0zvDncQ8` |
| layout | `agHlfrHspM3Zk0bcNd303v6ZZ3Pal5T9W4FYQ73P3Ok` |
| keyring | `V8NwnsYlo53Fpc2QKSgaiVBi6f-8lY1RnNvP7Qs0a0M` |

### 10.2 Seal and MAC

- Device seal of `{"computerName":"PC-0815","deviceId":"3f1a9e2c-7b44-4b2a-9e21-0c6a2f9b7e10","domain":"contoso.local","fingerprint":"ABAB…AB" (64 characters),"registered":1790000000}`
  with the record key: `yKkqub3rFuocZrgfS6T0f1Y2mzg4HnUKap1X-axjkBo`
- Enroll MAC `mac(enroll, org, "enroll", "3f1a9e2c-7b44-4b2a-9e21-0c6a2f9b7e10", "keyhash", "nonce")`:
  `pEvvn8W0OFIMj3ocfrzLTjd28ibkmiLVixZhqQAdqRM`
- Device secret text for the same bytes and id `s_abcdefgh`: `rhd1.o_aaaaaaaaaaaaaaaa.s_abcdefgh.AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8`
- Canonical JSON: `{"b":1,"a":[true,null,"x"],"é":"ü\n\"","A":{"z":0,"y":-1}}` →
  `{"A":{"y":-1,"z":0},"a":[true,null,"x"],"b":1,"é":"ü\\n\\""}` (as a JSON string literal)
