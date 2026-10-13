# RemoteHub privacy statement

RemoteHub sends a small amount of **anonymous** usage data and crash reports, so we can see how many installations are
in use, how they're used and what goes wrong. This page lists everything that is sent. Nothing else is.

You can turn it off at any time in *Settings → Updates & privacy* ("Send anonymous usage data and crash reports"). IT
departments can turn it off for everyone with the policy `DisableTelemetry` (DWORD 1) under
`HKLM\SOFTWARE\Policies\RemoteHub` or `HKCU\SOFTWARE\Policies\RemoteHub`.

## What is never sent

- No connection data: no server or host names, IP addresses, ports, connection, folder or share names, paths, notes or
  settings of a connection.
- No credentials: no user names, passwords, domains, certificates or keys.
- Nothing about what you do in a session: no screen content, keystrokes, files, commands, clipboard, recordings or
  audit log entries.
- Nothing that identifies you or your computer: no user, computer or domain name, MAC address, machine ID, serial
  number, e-mail address or Windows account.
- The RemoteHub Agent on your servers sends nothing at all.

## What is sent

Everything goes in the background as small HTTPS requests to `https://update.remotehub.dev/telemetry`. If a request
fails it isn't retried (except crash reports, see below), and RemoteHub never waits for it.

**With every request**

| Field | Example | Meaning |
|---|---|---|
| `event` | `app_start` | What the request is about (see below) |
| `version` | `0.19.0` | RemoteHub version |
| `channel` | `stable` / `beta` | Update channel |
| `os` | `Windows 11` | Only `Windows 10`, `Windows 11` or `Windows Server` – no build or edition |
| `arch` | `x64` | Processor type: `x64`, `arm64` or `x86` |
| `installId` | `3f2b…-…` | A random number, see below |

**`app_start` – once each time RemoteHub starts**, plus how many items your navigation holds. These are numbers only:

| Field | Meaning |
|---|---|
| `connections` | Number of connections |
| `agents` | Number of connections with the RemoteHub Agent installed |
| `shares` | Number of network shares (SMB, SFTP, FTP, WebDAV) |
| `folders` | Number of folders |

**`app_session` – how long RemoteHub ran last time**, sent at the next start:

| Field | Meaning |
|---|---|
| `runSeconds` | How many seconds the previous session was open. Time the PC was asleep doesn't count. |

`version` is then the version that ran in that session.

**`app_crash` – after a crash**, sent at the next start:

| Field | Meaning |
|---|---|
| `errorType` | The kind of error, e.g. `System.IO.IOException` or `NativeCrash` |
| `message` | The error message, cleaned (see below), at most 500 characters |
| `stack` | Where in RemoteHub's code it happened, cleaned, at most 8,000 characters |

Before a crash report is even saved on your PC, everything that could identify a person, computer or server is removed
from the message and stack: paths, user, computer and domain names, host names, IP and e-mail addresses and anything in
quotes. At most 5 reports wait to be sent, none older than 30 days. With usage data turned off they're deleted instead.

## The install ID

The install ID is a random number (GUID) created once and stored in `%LOCALAPPDATA%\RemoteHub\install-id`. It is not
derived from anything about your computer or you, so it only tells us that two requests came from the same
installation – not whose or which. Turning usage data off deletes it; turning it on again creates a new, unrelated one.

## What is stored on your PC

- `%LOCALAPPDATA%\RemoteHub\install-id` – the install ID.
- `%LOCALAPPDATA%\RemoteHub\run-time.json` – the version and run time of the current session, until it's sent.
- `%LOCALAPPDATA%\RemoteHub\crash\` – crash reports waiting to be sent.

All of these are removed when usage data is turned off (waiting crash reports at the next start).

## Your IP address

Like every request on the internet, the server sees the public IP address the request comes from. It is not stored
with the usage data and not used to identify or locate you.

## How long it is kept

Usage data and crash reports are stored on our server for 365 days and then deleted.

## Development builds

Debug builds of RemoteHub never send anything.
