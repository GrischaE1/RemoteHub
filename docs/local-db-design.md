# RemoteHub – local database

Status: phases 0–2 implemented (0.14.0), phases 3–5 open · Scope: RemoteHub app (not the agent) · Priorities:
**encryption, security, performance**

## 1. Goal

Replace the single settings file (`settings.dat` / `RemoteHub.settings`) with an encrypted local SQLite database, so
RemoteHub can store much more data – notes, audit logs, connection history, later scripts – without rewriting one big
file on every change.

Not in scope: sharing data between different users (comes with a server), scripts (own design; they may live in a
plain folder).

## 2. Today

| What | Where | Protection |
|---|---|---|
| Connections, folders, credentials, defaults | `%APPDATA%\RemoteHub\settings.dat` – one JSON document | DPAPI (current user), plus the startup-lock key if on |
| Same, synced across the user's devices | `<OneDrive>\RemoteHub\RemoteHub.settings` | AES-256-GCM, key from a password (PBKDF2-SHA256, 600,000 iterations); key cached per PC under DPAPI |
| Window/UI state | `local.json` | none (no secrets) |
| Startup lock | `lock.json` – random 256-bit key wrapped by password and optionally Windows Hello | PBKDF2 / TPM |

Every change rewrites and re-encrypts the whole document (debounced). Fine for connections; it doesn't scale to
thousands of notes or an audit trail.

## 3. Decisions

1. **SQLite with whole-file encryption: SQLite3 Multiple Ciphers** (MIT licence, actively maintained) through
   `SQLite3MC.PCLRaw.bundle`, used with `Microsoft.Data.Sqlite.Core` – not the full `Microsoft.Data.Sqlite`, which brings
   its own unencrypted SQLite and conflicts with a second SQLitePCLRaw bundle. Cipher scheme: **ChaCha20-Poly1305**
   (sqleet, the project's recommended default): every page is encrypted and authenticated. The full file is
   unreadable without the key – including indexes, full-text search and the write-ahead log.
   Not chosen: SQLCipher – the free .NET builds (`SQLitePCLRaw.bundle_e_sqlcipher`) are deprecated and unmaintained, and
   the supported .NET package is commercial.
2. **Secrets encrypted a second time** at field level (passwords), so they never appear in plain text in query results,
   indexes, exports or memory until used.
3. **The DB is local only** – `%LOCALAPPDATA%\RemoteHub\remotehub.db`. Never in OneDrive or a network folder (SQLite
   corrupts there). `%LOCALAPPDATA%`, not `%APPDATA%`, because Roaming can be redirected to a network share.
4. **Device sync stays file-based**, but per record instead of one big file (section 8).
5. **Existing keys are reused**: DPAPI and the startup lock (password / Windows Hello) protect the DB key the same way
   they protect `settings.dat` today. Nothing changes for the user when unlocking.

## 4. Key hierarchy

```
Windows account (DPAPI, CurrentUser)           Startup lock key (if on: password / Windows Hello)
            └──────────────┬───────────────────────────────┘
                     wraps (as settings.dat today)
                           ▼
              db.key  (%LOCALAPPDATA%\RemoteHub)
          ┌────────────────┴────────────────┐
     DB key (256-bit random)          Secret key (256-bit random)
     SQLite3MC, ChaCha20-Poly1305     AES-256-GCM per password field
     → whole DB file                  AAD = table | row id | column

Sync folder (OneDrive):  sync.key = sync master key (256-bit random) wrapped with the sync password
                         (PBKDF2-SHA256, 600,000 iterations). Cached per device inside db.key.
                         → each item file, AES-256-GCM, AAD = item id | kind | version
```

- All keys are random; only the wrapping keys come from passwords. Changing a password rewraps one small key file
  and re-encrypts nothing else.
- The DB key is random (256 bit), so the cipher's own password KDF adds no security. It is passed as a hex key and
  the KDF iterations are set to the minimum, so opening is fast; the slow, attack-resistant derivation happens once, in
  the startup lock. (Exact pragmas – `hexkey`, `kdf_iter` – to be confirmed against the SQLite3MC docs in phase 0.)
- The key never appears in a connection string that is logged or kept; the connection is opened with the key
  pragma and the string is cleared.
- Keys live in pinned byte arrays, are zeroed after use (`CryptographicOperations.ZeroMemory`) and are never logged.
- Turning the startup lock on or off rewraps `db.key` only (the same pattern as `SettingsStore.Reprotect` today).

## 5. Security measures

| Threat | Measure |
|---|---|
| Stolen laptop or disk image, copied DB file | Whole-file encryption; the key needs this Windows account (DPAPI) and, if on, the startup password or Windows Hello |
| Another local user, or malware running as another user | Folder ACL: only the user and SYSTEM. DPAPI is bound to the account |
| Malware running as the same user while RemoteHub is unlocked | Can't be prevented locally. Limited by field-level secret encryption (passwords decrypted only when used) and the startup lock with auto-lock |
| Tampering with the DB file | Per-page authentication (Poly1305): a changed page fails to read – no silent corruption |
| Tampering with sync items (swapped, replayed, rolled back) | AES-GCM with AAD binding id, kind and version; each device ignores versions older than the highest it has seen |
| Plain-text leftovers | `temp_store = MEMORY`; `secure_delete = ON`; the old `settings.dat` is deleted after a verified migration (section 9); backups are encrypted with the same key |
| Editing the audit log | Append-only table (triggers reject UPDATE/DELETE outside retention), hash chain (each row hashes the previous one); a verify function reports breaks. Note: this makes changes **detectable**, not impossible, for someone who holds the user's key. Tamper-proof audit needs a server or Event Log forwarding |
| SQL injection | Parameters only; no SQL built from strings; `trusted_schema = OFF`, extension loading off |
| Sensitive data in the audit log | Never stores passwords, clipboard content or file contents – only who, what, where, when, result |

## 6. Schema (version 1)

Queried fields are columns; the rest of a record is a JSON column, so the existing models (`RdpOptions` grows
often) are reused without a migration per new option.

```sql
meta              (key TEXT PRIMARY KEY, value TEXT)                       -- schema_version, device_id, created_utc
folders           (id TEXT PK, parent_id TEXT, name TEXT, sort INT, data TEXT,
                   version INT, modified_utc INT, dirty INT)
connections       (id TEXT PK, folder_id TEXT, name TEXT, address TEXT, sort INT, data TEXT,
                   agent_fingerprint TEXT, version INT, modified_utc INT, dirty INT)
credentials       (id TEXT PK, name TEXT, username TEXT, domain TEXT, version INT, modified_utc INT, dirty INT)
secrets           (owner_kind TEXT, owner_id TEXT, purpose TEXT, value BLOB,          -- field-encrypted
                   PRIMARY KEY (owner_kind, owner_id, purpose))
settings          (key TEXT PK, data TEXT, version INT, modified_utc INT, dirty INT)  -- Defaults, Tools
notes             (id TEXT PK, connection_id TEXT, folder_id TEXT, title TEXT, body TEXT,
                   created_utc INT, version INT, modified_utc INT, dirty INT)
notes_fts         FTS5 (title, body, content='notes')                               -- full-text search
note_files        (id TEXT PK, note_id TEXT, name TEXT, mime TEXT, size INT, sha256 BLOB, data BLOB)
connection_history(id INTEGER PK, connection_id TEXT, start_utc INT, end_utc INT, via TEXT, result TEXT)
audit             (id INTEGER PK, time_utc INT, device_id TEXT, account TEXT, event TEXT,
                   connection_id TEXT, target TEXT, detail TEXT, prev_hash BLOB, hash BLOB)
sync_items        (kind TEXT, id TEXT, base_version INT, file_stamp TEXT, PRIMARY KEY (kind, id))
conflicts         (id INTEGER PK, kind TEXT, item_id TEXT, data BLOB, from_device TEXT, time_utc INT)
```

- Passwords move out of the JSON (`SavedCredential.Password`, `CredentialSet.Password` inside options) into `secrets`;
  the JSON keeps no password.
- Indexes: `connections(folder_id)`, `connections(name)`, `notes(connection_id)`, `audit(time_utc)`,
  `audit(connection_id, time_utc)`, `connection_history(connection_id, start_utc)`.
- UI state stays in `local.json` (it is per device and holds no secrets).
- Scripts: no table yet – see the script design.

## 7. Performance

| Measure | Why |
|---|---|
| WAL mode, `synchronous = NORMAL` | Readers never block the writer; durable on crash, fast commits |
| One writer connection on a background queue; separate read connections | The UI thread never waits for the disk |
| Connection tree loaded fully at start (few thousand rows) | Same in-memory model as today; the tree stays instant |
| Note bodies and files loaded on demand; lists show title and a snippet | Startup cost doesn't grow with notes |
| Saving writes changed rows only | Phase 1 keeps today's model: on save, each record's content hash is compared with the DB, and only changed records are written, in one transaction. Updating "last connected" is a single-row update instead of a full-file rewrite |
| Audit and history written in batches (every second or 100 events) | Many events cost one transaction |
| Retention cleanup in chunks of 1,000 rows, in the background | No long locks |
| FTS5 for notes | Search across thousands of notes without scanning |
| Raw DB key | No KDF on open |

Targets and phase 0 results (SQLite 3.53.4, SQLite3MC 2.4.0, ChaCha20-Poly1305, WAL, x64 desktop with 22 cores,
file cache warm; a notebook will be slower – the margins below allow for that):

| Operation | Target | Encrypted | Plain SQLite | Overhead |
|---|---|---|---|---|
| Open DB (key + pragmas) | – | 4.7 ms | 4.3 ms | 8 % |
| Open DB and load 5,000 connections (~1.5 KB each) | < 150 ms | 48 ms | 24 ms | 105 % |
| Insert 5,000 connections, one transaction (migration) | – | 397 ms | 238 ms | 67 % |
| Save one changed connection, own transaction | < 10 ms | 0.06 ms | 0.04 ms | 43 % |
| Audit event incl. hash chain, batches of 100 | < 1 ms | 0.012 ms | 0.007 ms | 62 % |
| Full-text search, 10,000 notes (~300 words), top 50 with snippet | < 50 ms | 3.0 ms | 2.4 ms | 24 % |
| File size, 10,000 notes | – | 59 MB | 57 MB | 3.5 % |

The relative overhead of bulk operations is higher than the 15 % assumed earlier, but every absolute target is met by
a factor of 3 to 150. The relative target is dropped; the absolute ones stay as regression limits.

## 8. Sync across the user's devices (OneDrive)

The same user on a workstation and a notebook. The DB stays local on each device; the sync folder holds one encrypted
file per record:

```
<OneDrive>\RemoteHub\
  sync.key                    sync master key, wrapped with the sync password
  items\<random id>.rhi       one file per folder, connection, credential, setting or note
  files\<random id>.rhf       note attachments (text and pictures, max. 25 MB each)
  audit\<device id>\<yyyy-mm-dd>.rha   audit events of one device and day
```

- File names are random ids; names, addresses or kinds are only inside the ciphertext.
- **Push:** rows with `dirty = 1` are written as item files (temp file + rename), with version = known version + 1.
- **Pull:** `FileSystemWatcher` plus a scan every 60 s and when the window is activated. Changed files (size or
  time differs from `file_stamp`) are decrypted and applied if their version is newer.
- **Deletes:** a missing file deletes the local record if it has no local changes. No tombstones are needed, because
  each device only uploads what it changed itself. Deleted records stay in "Recently deleted" for 30 days.
- **Conflicts** (both devices changed the same record before syncing – rare for one user): the newer change wins; the
  other version goes into `conflicts` and can be restored from the record's history. OneDrive's own conflict copies
  (`<id>-<PC name>.rhi`) are detected and handled the same way.
- **Audit log (synced, per user):** each device writes only its own folder `audit\<device id>\`, one file per day,
  rewritten as events are added. No device ever writes another device's files, so there are no conflicts. Every
  device reads the others' files into its `audit` table, so the viewer shows the user's events from all devices.
  Each device keeps its own hash chain; the chains are verified per device. Retention deletes whole day files.
- **Not synced:** UI state, connection history, device keys.
- The sync folder is marked "always keep on this device", so OneDrive doesn't leave placeholders that would load
  slowly.
- The existing policy `SettingsFolder` keeps working and points to the sync folder.

## 9. Migration

On the first start of the new version:

1. Read the existing data with the current code (`settings.dat`, or `RemoteHub.settings` in sync mode, unlocking as
   today).
2. Write everything into the new DB in one transaction, moving passwords into `secrets`.
3. Verify: read the DB back, rebuild `AppData`, and compare it with the source (all records and fields equal).
4. Only then rename the old file to `settings.dat.migrated`. It stays encrypted as before and is deleted
   automatically after 30 days. If anything fails, the DB is deleted and the old file is used unchanged.
5. In sync mode: write the item files and `sync.key`; `RemoteHub.settings` is renamed to
   `RemoteHub.settings.migrated` and keeps its old password.

Downgrade: an older version doesn't read the DB. Before GA this is acceptable; the release notes say so.

Schema changes later: `meta.schema_version` plus numbered migration steps, each in a transaction, with a backup
before each one.

## 10. Backup and recovery

- Daily backup through SQLite's online backup API to `%LOCALAPPDATA%\RemoteHub\backup\` (same encryption, last 7
  kept).
- Manual export: an encrypted file protected with a password (the existing export format, extended).
- If the DB fails to open (HMAC error, disk fault): offer the latest backup, or a fresh pull from the sync folder.

## 11. Audit log (client side)

Events: connect/disconnect, password shown or copied, credentials changed, settings changed, export/import, agent
installed, file transfer, recording started/stopped, script run (later), unlock failed, sync conflict.

- Retention: 365 days by default; policy `AuditRetentionDays` (HKLM/HKCU\SOFTWARE\Policies\RemoteHub).
- Viewer: filter by time, connection, event; export to CSV.
- Agent-side audit stays in the device's Windows Event Log (already built).

## 12. Implementation

| Phase | Content | Done when |
|---|---|---|
| 0 | Spike with SQLite3MC: x64 and arm64 binaries, key pragmas, FTS5 and WAL encrypted, benchmark the targets in section 7 | **Done** – see section 14 |
| 1 | `DataStore` replacing `SettingsStore.Load/Save`; key handling; migration; backups. No visible change | **Done** (0.14.0) – see section 15. Migration on real data: still to be checked on a real install |
| 2 | Audit log + connection history (+ "unused connections" filter on real history) | **Done** (0.14.0) – see section 15 |
| 3 | Notes: per connection/folder, Markdown, attachments, full-text search | |
| 4 | Device sync with item files; migrating the sync file | Two-device tests pass |
| 5 | Scripts | Own design |

Tests: wrong key fails, tampered page fails, AAD swap fails, migration round-trip, kill during write, two simulated
devices with one folder (conflict, delete, OneDrive conflict copy, offline device), performance benchmarks.

## 13. Decided

1. Encrypted SQLite: SQLite3 Multiple Ciphers with ChaCha20-Poly1305 (section 3).
2. The audit log is per user and syncs across the user's devices (section 8).
3. Audit retention: 365 days by default, adjustable by policy.
4. Note attachments: max. 25 MB per file – mainly text and pictures.

## 14. Phase 0 results

Packages: `Microsoft.Data.Sqlite.Core` 10.0.12 + `SQLite3MC.PCLRaw.bundle` 2.4.0 (both MIT; pulls `SQLitePCLRaw.core`
3.0.2). RemoteHub has no other SQLite dependency today, so there is no bundle conflict. The bundle's
`SQLitePCLRaw.batteries_v2` is picked up by Microsoft.Data.Sqlite automatically – no init code needed.

| Check | Result |
|---|---|
| Native binaries | win-x64 (2.3 MB) and win-arm64 (2.0 MB) – PE machine types verified (0x8664, 0xAA64). RemoteHub ships x64 only today; arm64 is available if needed |
| Opening with the key | `PRAGMA cipher = 'chacha20'`, `PRAGMA kdf_iter = 1`, `PRAGMA hexkey = '…'`, before the first read. Read back: cipher chacha20, kdf_iter 1 |
| Cipher KDF | With the default `kdf_iter` (64,007) opening takes 34 ms instead of 4.7 ms – the KDF is skipped as planned, the key is random |
| Encryption at rest | A marker string written to tables, the FTS5 index and the audit table was **not found** in the DB file, the WAL (kept not checkpointed, 250 KB) or the SHM file – neither while open nor after closing. The file header is not `SQLite format 3` |
| Wrong key / no key | Both fail with SQLite error 26 "file is not a database" |
| Tampering | One flipped bit in any page tested (first, second, middle, last; start, middle and end of the page) fails as soon as the page is read (error 11, or 26 for page 1). Pages a query doesn't read are not checked – `PRAGMA quick_check` reads all of them and runs with the daily backup |
| Online backup | `BackupDatabase` into a destination opened with the key stays encrypted and opens with the key |
| FTS5 | Compiled in, works encrypted |
| Extension loading | Not compiled in |
| Pragmas | `journal_mode = WAL`, `synchronous = NORMAL`, `temp_store = MEMORY`, `secure_delete = ON`, `trusted_schema = OFF`, `foreign_keys = ON` all accepted |

Notes for phase 1:
- Pass the key with `PRAGMA hexkey` on an open connection, not with `Password=` in the connection string (that would
  keep the key in a string in memory and applies the KDF).
- Disable pooling for the writer connection or clear pools on lock, so no keyed connection outlives the unlock.
- Run `PRAGMA quick_check` with the daily backup; on failure offer the last good backup.

## 15. Phases 1 and 2 – as built (0.14.0)

Code: `src/RemoteHub/Data/` – `LocalDb` (open, keys, schema, re-encryption, backups), `DbKeys` / `SecretBox` (key file,
field-level encryption), `AppDataRecords` (model ↔ records, change tracking), `AuditLog` / `ConnectionHistory`.
`SettingsStore` keeps its API; for "this PC" it now reads and writes the database. Tests: `LocalDbTests`,
`AppLockTests`, `SharedSettingsTests`.

What differs from the plan above, and why:

| Plan | Built | Why |
|---|---|---|
| Separate `folders` and `connections` tables | One `nodes` table (kind = folder/connection); `credentials` and `settings` have the same columns | One code path for all records; queried fields (name, host, parent) are columns |
| Key passed with `PRAGMA hexkey` | `sqlite3_key` with the key bytes | The key never exists as a string |
| Passwords decrypted only when used | Decrypted when loading, into the existing in-memory model | The app's model holds passwords today; changing that is a larger refactor (open) |
| Folder ACL: only the user and SYSTEM | The profile's default ACL (user, SYSTEM, Administrators) | Administrators can take any file anyway; the protection is DPAPI and the startup lock |
| Rekey: new DB key and new secret key | New DB key only | Passwords are only reachable through the newly encrypted pages; one key file write keeps it crash-safe (`db.key.next`) |
| Sync of the audit log (phase 4) | Not yet – per device until phase 4 | Phase 4 |
| "Unused" filter on the history table | Still uses the last connection time | The same event sets both; the history adds counts and durations (shown in the audit viewer for one connection) |

Shared-folder mode (phase 4 open): connections stay in `RemoteHub.settings` as before; the database holds this PC's own
settings (the fallback when the shared file is unavailable), the audit log and the history. A save with no changes
no longer rewrites the shared file.

Measured (Release, this PC, 5,000 connections in 50 folders, ~300-character notes, every tenth with its own password):

| Operation | Target | Measured |
|---|---|---|
| Open + load | < 150 ms | 116 ms |
| Save one change (whole path: compare all records, write the changed one) | < 10 ms (DB write) | 23 ms in total; the database write itself is well under 1 ms – the rest is serialising and hashing all records to find the change (in parallel) |
| First save / migration of 5,000 records | – | ~0.5 s, once |

Recovery paths (tested): wrong or missing key → "can't be opened" with the backup offer; a damaged page found while
loading → same; interrupted re-encryption → the matching key is found and kept; failed migration → settings.dat stays
in use.

Known limits: failed unlock attempts are written to the audit log only when the next unlock succeeds (before, the
database can't be opened); the audit log is tamper-evident, not tamper-proof (see section 5).
