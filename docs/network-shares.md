# Network shares

Status: brought in line with 0.22.0 (2026-10-27). A server in the navigation can have network shares below it, and a
share can stand on its own in a folder (0.18.0). Each one opens in a file tab with the file transfer layout: this PC on
the left, the share on the right. RemoteHub connects to the share itself – directly, or through a jump host (§5) – so
the device needs neither the RemoteHub Agent nor PowerShell remoting. SSH connections (0.22.0, §6) are kept the same
way and open a terminal tab; SSH and SFTP can sign in with a private key (§6.2).

## 1. What a share is

`NetworkShare`, stored inside its server's record (`Connection.Shares`), so the database schema doesn't change:

| Field | Meaning |
|---|---|
| Name | Shown below the server and in the tab row |
| Protocol | SMB, SFTP, FTP/FTPS, WebDAV – or SSH (§6) |
| Host, Port | Empty means the server's host and the protocol's default port (445, 22, 21/990, 443/80). SMB always uses 445 – another port is refused when opening ("SMB always uses port 445 – remove the port from the share.") |
| Path | SMB: `share` or `share\folder` – the share name is required; a pasted `\\host\share\folder` fills in the host (when it is empty or only the server's) and the rest. SFTP/FTP: the start folder (empty means home). WebDAV: the URL path |
| FtpSecurity | Explicit TLS (default, port 21), implicit TLS (port 990), or none (the editor warns) |
| WebDavHttps | On by default; off sends everything in plain text (the editor warns) |
| Account | Inherit (the server's credentials, which inherit as usual), a saved credential, or one entered for the share; for SSH and SFTP with a private key (§6.2) |
| TrustedFingerprint | SFTP host key, or an FTPS/WebDAV certificate Windows doesn't trust, once the user has accepted it |

Passwords are taken out of the JSON and sealed in `secrets` like every other password, at the path
`Shares/<n>/Credentials/Password`. A password that isn't stored is asked for when the share opens ("No password is
stored for <user>. Enter it to open <server> › <share> – it is kept in memory only.") and kept in memory for that tab
only – its reconnects and transfers reuse it.

No account: SMB and WebDAV use the Windows account, FTP signs in anonymously (`anonymous` / `anonymous@`). SFTP and SSH
need an account ("<share> (SFTP) needs an account. …").

### 1.1 Shares on their own (0.18.0)

A share doesn't need a server above it: **New → New connection → SFTP / FTP / SMB / WebDAV** (0.22.0; before, *New
network share*) puts it in the selected folder (or at the top level), shown like a connection with its address on a second line. `NetworkShare` derives from `NodeBase`, so such a
share is a tree node of its own (`$type` `share`, its own `nodes` record with kind `share`): folders, drag & drop,
search, duplicate and the colour tag work as for connections. Differences from a server's share:

- The host is required (a pasted `\\host\share` fills it in).
- Inherit means the folders' account (then the global defaults), like a connection.
- Its tab needs a `Connection` (tab group, colour, history): RemoteHub uses a stand-in per share that isn't stored,
  with the share's ID, name, host and folder.
- Dropped onto a connection it lands next to it – a drop never makes it a server's share. *Move under* in its context
  menu puts it under a jump host.

A server's shares stay inside its record (no `$type`) and are never selected, dragged or edited as nodes.

## 2. Protocols

| Protocol | Client | Sign-in | Notes |
|---|---|---|---|
| SMB | Windows' own client (`WNetAddConnection2` without a drive letter) | Kerberos/NTLM, whatever Windows negotiates; no account means the Windows account | Encryption and signing as the server requires |
| SMB, second account | SMBLibrary (LGPL, unchanged DLL) | NTLMv2; every request signed and every answer's signature checked, also when the server doesn't require it; a guest session is refused | Used automatically when Windows reports error 1219, meaning it already uses another account for that server |
| SFTP | SSH.NET | A private key (0.22.0, §6.2), the password and keyboard-interactive – the key first; an account is required | The host key is pinned per share after the user confirms it; a changed key is asked about with a warning |
| FTP/FTPS | FluentFTP | Password; anonymous if no account is set | Security None / Explicit TLS (21) / Implicit TLS (990); data connections encrypted too. A certificate Windows doesn't trust needs the user's OK, which is remembered per share; a data connection must present that certificate (or one Windows trusts) – it is refused without asking otherwise |
| WebDAV | Own client on `HttpClient` (PROPFIND, MKCOL, MOVE, DELETE, GET, PUT) | Basic, Digest, NTLM, Negotiate; no account means the Windows account | Same certificate handling as FTPS. Redirects aren't followed: a file path answered with a redirect is read as a folder; otherwise "The server redirects to <address> – use that address for the share." Status codes are said in words (401 user name or password, 403 access denied, 404/409 not found, 405 not allowed here, 412 already exists, 423 locked, 507 no space left) |

The tab's status names the client: "SMB · Windows", "SMB · Windows · your Windows account", "SMB · RemoteHub client
(Windows already uses another account for this server)", "SFTP · …", "WebDAV · HTTPS" / "WebDAV · HTTP, not
encrypted".

Windows SMB connections (`WNetAddConnection2`, only with an account set) get no drive letter and are shared per share
(`\\host\share`) and counted: the last tab or transfer using one removes it, and RemoteHub removes any that are left when
it exits. A connection that existed before RemoteHub (Explorer, `net use`) is used but never removed. Access denied on
the share adds a hint: a local administrator signing in over the network gets no administrator rights there (Windows'
remote UAC) – give the account itself access to the share.

**Keys and certificates:** the first time, "Trust this server?" shows the SHA-256 fingerprint and details; a different
one later asks "Server key changed" with a warning. Either way nothing is sent until the user accepts ("The server's
key wasn't accepted – nothing was sent."). The share's editor shows the accepted key or certificate with **Forget** (ask
again next time).

## 3. Connection lifetime

- **Browsing:** one connection per tab. It is closed after 5 minutes without use (`ShareSession.IdleTime`), and the
  next action opens it again without asking (the password stays in memory). The tab stays "connected" and its status
  says the connection is idle.
- **Transfers:** a separate connection per batch, closed when the batch ends. Each file – uploaded to the share or
  downloaded to this PC – is written as `<name>.remotehub-part`, its size is checked ("… size checked"), and then it is
  renamed; a wrong size fails the file and the part file is deleted. Existing items without overwrite are skipped
  ("Skipped – the file already exists", "Skipped – a folder has that name"). After a cancel or a lost connection, the
  next file connects again.
- **Closing the tab** closes everything.

## 4. UI

- **Server's context menu:** "Add network share…". The server tab's "+" menu lists its shares and also has "Add
  network share…".
- **New → New connection** (the New button, *New* in the tree's menu and in a share's own menu – one list, built in
  `FillNewConnection`) offers every kind this PC's policy allows: *Remote Control* (a Windows device, Ctrl+N), *SSH*,
  then *SFTP*, *FTP / FTPS*, *SMB* and *WebDAV*. A share kind opens the share editor with that type chosen; the type can
  still be changed there.
- **Share's context menu:** Open, Copy address, Edit…, Remove; a share on its own also has Duplicate, *Move under* and
  New. Removing a share deletes nothing on the share itself (an open tab of it is closed).
- **In the navigation:** double-click or Enter opens a share, F2 edits it, Del removes it.
- **The tab:** reuses `FileBrowserView` through `IRemoteFiles`, so drag & drop, the arrows, rename, delete,
  new folder and the transfers panel all work as in the file transfer tab. Drag & drop also works between a share tab
  and any other file tab.

## 5. Behind a jump host

A share of a server below a jump host, or a share on its own below one, is reached through the jump host's RemoteHub
Agent ([jump-host-design.md](jump-host-design.md) §15). The route is asked again on every reconnect (the share may have
moved); the jump host's password is asked once and kept in memory.

- **SMB:** always RemoteHub's own client (SMBLibrary) through a local port – Windows' client can't use another port, and
  `127.0.0.1:445` is this PC's own server. The tab says "SMB · RemoteHub client · via JUMP01".
- **SFTP and SSH:** through a local port; the host key is checked against the item's own host as for a direct connection.
- **WebDAV:** in-process through the tunnel; URL, certificate check and sign-in keep the share's host name.
- **FTP:** refused – *Open* is off in the menu with the reason "Reached through JUMP01. FTP can't go through a jump host
  – its data connections go to addresses the server announces. SMB, SFTP and WebDAV can."; opened anyway, the same
  text. The share stays saved.
- The tab's status ends with "· via JUMP01"; the tree shows "via JUMP01" beside the address of a share on its own.
- The share editor's host field explains the route: a jump host's own share on another host is "Reached directly, like
  JUMP01 itself. …"; a child's share on another host is "Another device, reached through JUMP01 like SQL01."

## 6. SSH connections (0.22.0)

An SSH connection opens a terminal on a Linux or macOS server, a NAS or a network device – no agent, no PowerShell.

### 6.1 What it is

A `NetworkShare` with `Protocol = Ssh`, stored like a share on its own (`$type` `share`): host, port (22), account, the
accepted host key, the folder and the jump host work as for SFTP (an older version doesn't know the kind – pre-GA, not
supported). It is only ever on its own – the share editor doesn't offer SSH for a server's share – and *Open*
(double-click, Enter) opens a terminal tab instead of a file tab. The share editor is its editor: the title says *New
SSH connection*, the path field is hidden, and switching between SSH and SFTP keeps the accepted host key (same host and
port, same server key); any other change of type, host or port asks again.

### 6.2 Private keys (SSH and SFTP)

- **Where:** *Enter an account for this connection* in the editor, and every saved credential (*Settings → Security &
  storage → Manage saved credentials…*), have **Import key…**, **Remove key** and a **Key passphrase** box. A folder
  passes on a saved credential's key like its password. Formats: OpenSSH, PEM (PKCS#1, PKCS#8, EC) and PuTTY `.ppk`
  (SSH.NET); a `.pub` file, a binary file or one over 64 KB is refused when it is picked.
- **Stored:** the key file's text is read once into a `Secret` (`CredentialSet.PrivateKey`) and sealed in `secrets`
  like a password (path `Credentials/PrivateKey`; saved credentials `PrivateKey`) – the database record never holds it
  (`AppDataRecords.Extract` moves every captured secret, not only fields named Password). The file isn't needed
  afterwards. The editors show what it is, never the key: type, size and SHA-256 fingerprint as `ssh-keygen -l` writes
  it, to compare with the server's `authorized_keys`.
- **Passphrase:** optional; checked when saved ("The passphrase is wrong …"). Not stored, it is asked when connecting
  and kept in memory for that tab only. `AllowSavedPasswords` = 0 treats it as a password: the box is greyed out, a
  stored one isn't used and is removed at the next start. The key itself stays – it isn't a password.
- **Sign-in:** with a key, SSH.NET offers it first; the password (and keyboard-interactive) only when one is set or
  stored – a key-only account isn't asked for a password. A refused sign-in says what was tried ("The server didn't
  accept the private key for this user (is its public key in the user's authorized_keys?)"). FTP, SMB and WebDAV
  ignore a key an inherited account carries.

### 6.3 The terminal

- **Emulator:** RemoteHub's own (`Terminal/VtTerminal`, xterm-compatible): UTF-8, wide characters, SGR with 16, 256
  and 24-bit colours, scroll regions, the alternate screen (vim, less, htop), DEC line drawing, bracketed paste, mouse
  reporting (1000/1002/1003, SGR 1006), 5,000 lines of scrollback. No third-party terminal code: the only packaged
  Windows Terminal control is an unverified repackage.
- **Untrusted output:** everything the server sends is bounded – 32 parameters per sequence, values capped at 65535,
  repeats at one screen; OSC, DCS, APC, PM and SOS strings are read and dropped without being kept. Nothing it sends
  reaches this PC: no clipboard (OSC 52), no window title, no answers about the window (`CSI t`). It answers only
  device attributes, status and the cursor position.
- **Shell:** `xterm-256color`, the tab's size in cells, sent again after a resize (once the resizing stops); keep-alive
  every 30 s. Output is read on a thread of its own and drawn at most once per frame.
- **Keys:** xterm's – arrows and Home/End in normal or application mode, function keys, modifiers (`CSI 1;5D`),
  Ctrl+letter as control characters, Alt as Meta (ESC prefix); AltGr (Ctrl+Alt) is text. With the terminal focused the
  window's own shortcuts (Ctrl+N, Ctrl+W, Ctrl+F …) go to the terminal; Ctrl+Tab still switches tabs.
- **Copy and paste:** select with the mouse (double-click a word, triple-click a line; Shift while a program uses the
  mouse), Ctrl+Shift+C / Ctrl+Insert copies, Ctrl+Shift+V / Shift+Insert or the menu pastes. With bracketed paste,
  escape characters are taken out of the paste so it can't end itself early; without it, a paste of several lines asks
  first ("Each one runs as a command on <name> as soon as it is pasted").
- **The tab:** the remote tool header (*SSH · <address>*, the transport badge "SSH · OpenSSH_9.6 · via JUMP01", REC),
  the shared *not connected* overlay when the shell ends ("The session ended.") or the connection drops, and
  recording like the other tabs. Screen readers and the lab read the screen through UI Automation (a read-only Value).
- **Log:** DiagLog says "SSH: connected", "SSH: session ended" – no host, user or command.

### 6.4 Policy

`AllowSsh` (*Console → Features → Connections*, 0.22.0): 0 hides SSH under New and refuses opening; saved SSH
connections stay in the tree. It is a switch of its own – `AllowNetworkShares` doesn't turn SSH off, and `AllowSsh`
doesn't turn off SFTP (`AllowSftp`).

## 7. Not done yet

- SSH agent forwarding, port forwarding, X11, and certificates (OpenSSH CA-signed keys) – keys only.
- A private key for FTPS (a TLS client certificate).
- NFS: it signs in by the client's user ID, not with an account, and needs Windows' Client for NFS.
- Resuming a broken transfer (it starts again).
- FTP through a jump host.
