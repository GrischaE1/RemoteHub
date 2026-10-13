# Network shares

Status: 0.17.0. A server in the navigation can have network shares below it. Each one opens in a file tab
with the file transfer layout: this PC on the left, the share on the right. RemoteHub connects to the share directly,
so the device needs neither the RemoteHub Agent nor PowerShell remoting.

## What a share is

`NetworkShare`, stored inside its server's record (`Connection.Shares`), so the database schema doesn't change:

| Field | Meaning |
|---|---|
| Name | Shown below the server and in the tab row |
| Protocol | SMB, SFTP, FTP/FTPS, WebDAV |
| Host, Port | Empty means the server's host and the protocol's default port (445, 22, 21/990, 443/80) |
| Path | SMB: `share` or `share\folder`. SFTP/FTP: the start folder (empty means home). WebDAV: the URL path |
| FtpSecurity | Explicit TLS (default), implicit TLS, or none |
| WebDavHttps | On by default; off sends everything in plain text (the editor warns) |
| Account | Inherit (the server's credentials, which inherit as usual), a saved credential, or one entered for the share |
| TrustedFingerprint | SFTP host key, or an FTPS/WebDAV certificate Windows doesn't trust, once the user has accepted it |

Passwords are taken out of the JSON and sealed in `secrets` like every other password, at the path
`Shares/<n>/Credentials/Password`. A password that isn't stored is asked for when the share opens and kept in memory
for that tab only.

### Shares on their own (0.18.0)

A share doesn't need a server above it: **New → New network share** puts it in the selected folder (or at the top
level), shown like a connection with its address on a second line. `NetworkShare` derives from `NodeBase`, so such a
share is a tree node of its own (`$type` `share`, its own `nodes` record with kind `share`): folders, drag & drop,
search, duplicate and the colour tag work as for connections. Differences from a server's share:

- The host is required (a pasted `\\host\share` fills it in).
- Inherit means the folders' account (then the global defaults), like a connection.
- Its tab needs a `Connection` (tab group, colour, history): RemoteHub uses a stand-in per share that isn't stored,
  with the share's ID, name, host and folder.

A server's shares stay inside its record (no `$type`) and are never selected, dragged or edited as nodes.

## Protocols

| Protocol | Client | Sign-in | Notes |
|---|---|---|---|
| SMB | Windows' own client (`WNetAddConnection2` without a drive letter) | Kerberos/NTLM, whatever Windows negotiates; no account means the Windows account | Encryption and signing as the server requires |
| SMB, second account | SMBLibrary (LGPL, unchanged DLL) | NTLMv2 | Used automatically when Windows reports error 1219, meaning it already uses another account for that server |
| SFTP | SSH.NET | Password, keyboard-interactive | The host key is pinned per share after the user confirms it; a changed key is asked about with a warning |
| FTP/FTPS | FluentFTP | Password; anonymous if no account is set | A certificate Windows doesn't trust needs the user's OK, which is remembered per share |
| WebDAV | Own client on `HttpClient` (PROPFIND, MKCOL, MOVE, DELETE, GET, PUT) | Basic, Digest, NTLM, Negotiate; no account means the Windows account | Same certificate handling as FTPS |

The Windows SMB connections are counted per share. The last tab or transfer using one disconnects it, and RemoteHub
disconnects any that are left when it exits. A connection that existed before RemoteHub (Explorer, `net use`) is used
but never disconnected.

## Connection lifetime

- **Browsing:** one connection per tab. It is closed after 5 minutes without use (`ShareSession.IdleTime`), and the
  next action opens it again without asking (the password stays in memory). The tab stays "connected" and its status
  says the connection is idle.
- **Transfers:** a separate connection per batch, closed when the batch ends. Each file is written as
  `<name>.remotehub-part`, its size is checked, and then it is renamed. After a cancel or a lost connection, the next
  file connects again.
- **Closing the tab** closes everything.

## UI

- **Server's context menu:** "Add network share…". The server tab's "+" menu lists its shares and also has "Add
  network share…".
- **Share's context menu:** Open, Copy address, Edit…, Remove. Removing a share deletes nothing on the share itself.
- **In the navigation:** double-click or Enter opens a share, F2 edits it, Del removes it.
- **The tab:** reuses `FileBrowserView` through `IRemoteFiles`, so drag & drop, the arrows, rename, delete,
  new folder and the transfers panel all work as in the file transfer tab. Drag & drop also works between a share tab
  and any other file tab.

## Not in 0.17.0

- SSH keys for SFTP (password only).
- NFS: it signs in by the client's user ID, not with an account, and needs Windows' Client for NFS.
- Resuming a broken transfer (it starts again).
