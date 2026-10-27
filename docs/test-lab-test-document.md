# RemoteHub test lab: full test document

Every test the lab has to run before a release, each with its preconditions, steps and expected result. At the start you'll find a comparison with the automated tests that exist today.

This document goes into more detail than the test sheet [test-lab-cases.md](test-lab-cases.md). The sheet keeps the short "pass when" rows that the release gate and `lab\New-LabReport.ps1` read. This document gives the steps behind each row, and the IDs and order are the same. When the two differ, the steps here are the ones to use. Fix the sheet row and keep the ID.

Generated on 2026-10-13. The case text was written from the product code (RemoteHub, RemoteHub Agent), the lab module and the existing Pester files.

## How to read a case

```
#### <ID> · <title>
P<1-3> · How: <method> · Needs: <extra setup> · Status: <status>
**Preconditions:** ...
**Steps:** numbered, each one a single action of the host, the harness or a simulated user
**Expected:** one bullet per check. The case passes only when every bullet holds.
Gap:  (automated cases only) what the existing test does not check yet
Note: where the code differs from the sheet, or what the lab still lacks to run the case
```

| Field | Values |
|---|---|
| P | 1 = release blocker, 2 = must pass before a minor release, 3 = nice to have |
| How | PS = PowerShell Direct into the guest; H = lab harness (`tests\RemoteHub.LabHarness`, RemoteHub's own client code); C = Hyper-V console (keyboard, screenshot); S = simulated user (task in the user's session); U = UI Automation on RemoteHub's window; N = network/raw probe; M = MSI/msiexec; E = event log / evidence files |
| Status | ✅ Automated = a Pester `It` with this ID exists; ○ = not automated yet |
| Needs | 2VM = a second VM (client + device); extra VMs, policies or tools the case needs |

The ID is also the Pester tag. Each `It` name starts with its ID. `New-LabReport.ps1` uses that to match the results to the sheet.

## Comparison with the existing automated tests

**397 cases in total, 85 automated (21%).** 85 Pester tests carry a case ID, in 9 files under `lab\tests`.

| Area | Cases | P1 | Automated | Not automated | Coverage |
|---|--:|--:|--:|--:|--:|
| Lab self-check (SMOKE) | 10 | 7 | 7 | 3 | 70% |
| Agent install, update, uninstall (A) | 75 | 33 | 33 | 42 | 44% |
| Agent sign-in and transport security (S) | 23 | 12 | 0 | 23 | 0% |
| Agent PowerShell and remote tools (P, T) | 31 | 8 | 22 | 9 | 71% |
| WinRM setup script (W) | 15 | 5 | 11 | 4 | 73% |
| Agent remote desktop (D) | 129 | 54 | 0 | 129 | 0% |
| RemoteHub client, RDP, UI end to end (C, R, UI) | 102 | 41 | 0 | 102 | 0% |
| Network shares (S-SHARE, to be renamed F-) | 12 | 2 | 12 | 0 | 100% |
| **Total** | **397** | **162** | **85** | **312** | **21%** |

Release blockers (P1): 44 of 162 automated.

### Consistency checks

- Pester IDs missing from this document: none
- Cases marked ✅ with no Pester test: none
- Pester tests whose case is still marked ○: none
- Pester tests without a case ID (the report can't match them): none
- Duplicate IDs in this document: none

### Existing tests and what they still miss

Each automated case was checked against its expected result. **Gap** lists what the current test doesn't assert yet. An empty cell means the test checks everything the case asks for.

| ID | Title | Test file | Gap |
|---|---|---|---|
| SMOKE-01 | Console account after revert | `Smoke.Tests.ps1` |  |
| SMOKE-02 | Admin token | `Smoke.Tests.ps1` | the sheet also asks for High integrity (`S-1-16-12288` / "Mandatory Label\High Mandatory Level"); the test does not check the integrity level. |
| SMOKE-03 | Standard user | `Smoke.Tests.ps1` |  |
| SMOKE-04 | Address | `Smoke.Tests.ps1` | the sheet also asks that the host reaches the address (TCP 445 or ICMP); the test does not try to connect. |
| SMOKE-05 | Auto-logon | `Smoke.Tests.ps1` |  |
| SMOKE-06 | Code in the user's session | `Smoke.Tests.ps1` |  |
| SMOKE-07 | Lock / unlock | `Smoke.Tests.ps1` |  |
| A-INST-01 | Install over remoting (`InstallOverRemoting`) | `A-INST.Tests.ps1` |  |
| A-INST-02 | Service | `A-INST.Tests.ps1` |  |
| A-INST-03 | Health | `A-INST.Tests.ps1` | the test does not check `version` = MSI version, `protocol` = 1, or `features` containing `powershell` and `desktop`. |
| A-INST-04 | Certificate | `A-INST.Tests.ps1` | the test does not check RSA 3072, validity of 2 years (`NotAfter` is read but not compared) or EKU server authentication (1.3.6.1.5.5.7.3.1). |
| A-INST-05 | agent.json | `A-INST.Tests.ps1` | the test does not check `deviceLabel` (= VM ID), `certificateThumbprint` (only used to find the certificate), `virtualDisplay` (`auto`), `logRetentionDays` 30 or `maxSessions` 20. |
| A-INST-06 | Firewall | `A-INST.Tests.ps1` | "all profiles" is not checked (the rule's `Profile` is read but not compared with `Any`). |
| A-INST-07 | Folder ACL | `A-INST.Tests.ps1` |  |
| A-INST-08 | No secrets left | `A-INST.Tests.ps1` | the test does not search for PFX files, does not look for `.rhagent` in `C:\Windows\Temp`, and does not check that the cached MSI in `C:\Windows\Installer` contains no PFX. |
| A-INST-09 | Event source | `A-INST.Tests.ps1` | the sheet asks for the start line; the test checks the enroll line. Not checked: "RemoteHub Agent <version> starting on port 58732 (device <deviceId>, certificate <SHA-256>)". |
| A-UPD-01 | Update through the agent (`StartUpdateThroughAgent`) | `A-UPD.Tests.ps1` | "down within 90 s" is not asserted. `WaitForRestartAsync` moves on after 90 s even if the agent never went down. |
| A-UPD-02 | Temp folder removed | `A-UPD.Tests.ps1` |  |
| A-UPD-03 | Identity kept | `A-UPD.Tests.ps1` |  |
| A-UPD-04 | New device certificate on update | `A-UPD.Tests.ps1` |  |
| A-UPD-05 | Update over remoting | `A-UPD.Tests.ps1` |  |
| A-UPD-06 | Update keeps policies and logs | `A-UPD.Tests.ps1` |  |
| A-UPD-07 | Update with virtual display change | `A-UPD.Tests.ps1` | "empty keeps it" is not tested. Note: RemoteHub always passes `VIRTUALDISPLAY=1` or `0` (`InstallScript` / `DetachedInstallScript`), never empty. "Empty" only happens with a hand-run msiexec. |
| A-UPD-08 | Update during an open PowerShell session | `A-UPD.Tests.ps1` |  |
| A-UPDFAIL-01 | Broken MSI through the agent | `A-UPDFAIL.Tests.ps1` |  |
| A-UPDFAIL-02 | Upgrade that fails in enroll | `A-UPDFAIL.Tests.ps1` | the test does not check the enroll error text ("enroll failed: The enrollment has no valid device ID.") in the agent log or the MSI log. |
| A-UPDFAIL-04 | File that isn't a package | `A-UPDFAIL.Tests.ps1` |  |
| A-SAME-01 | Reinstall the same version | `A-UPD.Tests.ps1` | the expected version equals the running version, so `updated` doesn't prove a reinstall happened. The test doesn't check a restart (for example a new agent PID or a second "starting" log line). |
| A-SAME-02 | Same version with a new device certificate | `A-UPD.Tests.ps1` |  |
| A-DOWN-01 | Downgrade | `A-UPD.Tests.ps1` |  |
| A-UNINST-01 | Installed before | `A-UNINST.Tests.ps1` | the sheet says "health answers"; the test does not call `/v1/health` before the uninstall. |
| A-UNINST-02 | `msiexec /x /qn` | `A-UNINST.Tests.ps1` |  |
| A-UNINST-03 | Product, service, folder | `A-UNINST.Tests.ps1` |  |
| A-UNINST-04 | Firewall rule | `A-UNINST.Tests.ps1` |  |
| A-UNINST-05 | Certificates | `A-UNINST.Tests.ps1` |  |
| A-UNINST-06 | agent.json | `A-UNINST.Tests.ps1` |  |
| A-UNINST-07 | Logs kept | `A-UNINST.Tests.ps1` | the test checks that the line exists, not that it is the last line. |
| A-UNINST-08 | Event source | `A-UNINST.Tests.ps1` |  |
| A-UNINST-09 | Port | `A-UNINST.Tests.ps1` |  |
| A-UNINST-10 | SoftwareSASGeneration | `A-UNINST.Tests.ps1` | only the "agent set it" branch is tested. The "kept when a policy set it" branch (value set before the install, no marker) is not covered here (see A-INST-23). |
| P-SES-01 | Session | `P-SES.Tests.ps1` |  |
| P-SES-02 | Profile + environment | `P-SES.Tests.ps1` |  |
| P-SES-03 | End kills the tree | `P-SES.Tests.ps1` |  |
| P-SES-07 | 20 sessions in parallel | `P-SES.Tests.ps1` |  |
| P-SES-09 | Tab completion | `P-SES.Tests.ps1` |  |
| T-FS-01 | List drives / folders | `T-TOOLS.Tests.ps1` |  |
| T-FS-02 | Upload small files | `T-TOOLS.Tests.ps1` |  |
| T-FS-03 | Upload large file | `T-TOOLS.Tests.ps1` |  |
| T-FS-05 | Skip existing / overwrite | `T-TOOLS.Tests.ps1` |  |
| T-FS-06 | Cancel upload | `T-TOOLS.Tests.ps1` |  |
| T-FS-07 | Download | `T-TOOLS.Tests.ps1` |  |
| T-FS-08 | Rename / delete / new folder | `T-TOOLS.Tests.ps1` |  |
| T-REG-01 | Browse | `T-TOOLS.Tests.ps1` |  |
| T-REG-02 | Value types | `T-TOOLS.Tests.ps1` |  |
| T-REG-04 | Export | `T-TOOLS.Tests.ps1` |  |
| T-REG-05 | Hive protection | `T-TOOLS.Tests.ps1` |  |
| T-SVC-01 | List | `T-TOOLS.Tests.ps1` |  |
| T-SVC-02 | Start / stop / restart | `T-TOOLS.Tests.ps1` |  |
| T-SVC-03 | Startup type | `T-TOOLS.Tests.ps1` |  |
| T-PROC-01 | List | `T-TOOLS.Tests.ps1` |  |
| T-PROC-02 | End process / tree | `T-TOOLS.Tests.ps1` |  |
| T-PROC-03 | Critical processes | `T-TOOLS.Tests.ps1` |  |
| W-HTTPS-01 | HTTPS setup | `W-SETUP.Tests.ps1` |  |
| W-HTTPS-02 | Certificate | `W-SETUP.Tests.ps1` |  |
| W-HTTPS-03 | Fetched certificate matches | `W-SETUP.Tests.ps1` |  |
| W-HTTPS-04 | Session pinned | `W-SETUP.Tests.ps1` |  |
| W-HTTPS-05 | Firewall | `W-SETUP.Tests.ps1` |  |
| W-TWICE-01 | Run twice | `W-SETUP.Tests.ps1` |  |
| W-PRE-01 | Built-in rule on before | `W-SETUP.Tests.ps1` |  |
| W-LISTENER-01 | Existing HTTPS listener | `W-SETUP.Tests.ps1` |  |
| W-DRY-01 | `$DryRun = $true` | `W-SETUP.Tests.ps1` |  |
| W-LEGACY-01 | Old rule name | `W-SETUP.Tests.ps1` |  |
| W-LATFP-01 | LocalAccountTokenFilterPolicy option | `W-SETUP.Tests.ps1` |  |
| S-SMB-01 | Opens a share through Windows and copies both ways | `S-SHARE.Tests.ps1` |  |
| S-SMB-02 | Uses RemoteHub's own client for a second account on the same server | `S-SHARE.Tests.ps1` |  |
| S-SMB-03 | The standard user copies both ways on the share it may use | `S-SHARE.Tests.ps1` |  |
| S-SMB-04 | A wrong password is reported as such | `S-SHARE.Tests.ps1` |  |
| S-SFTP-01 | Asks about the host key once and copies both ways | `S-SHARE.Tests.ps1` |  |
| S-SFTP-02 | Connects without asking once the key is accepted | `S-SHARE.Tests.ps1` |  |
| S-SFTP-03 | Refuses a key that differs from the accepted one when the user says no | `S-SHARE.Tests.ps1` |  |
| S-FTP-01 | Asks about the self-signed certificate once and copies both ways over TLS | `S-SHARE.Tests.ps1` |  |
| S-FTP-02 | Sends nothing when the certificate is refused | `S-SHARE.Tests.ps1` |  |
| S-DAV-01 | Copies both ways over HTTPS with Windows authentication | `S-SHARE.Tests.ps1` |  |
| S-DAV-02 | Copies both ways over HTTP | `S-SHARE.Tests.ps1` |  |
| S-DAV-03 | A wrong password is reported as such | `S-SHARE.Tests.ps1` |  |

15 of 85 automated cases have a gap.

### Not automated yet, by priority

Regenerated 2026-10-15 from the sheet: ○ = planned, no test yet.

<details><summary><b>P1</b>: 3 cases</summary>

- **Agent remote desktop (D)** (3): D-SHELL-02, D-SHELL-03, D-SHELL-05

</details>

<details><summary><b>P2</b>: 2 cases</summary>

- **Agent remote desktop (D)** (2): D-SHELL-01, D-SHELL-04

</details>

### Written, not yet run in the lab

◐ = the test exists but has not run in the lab yet. Regenerated by `lab\Update-LabCaseStatus.ps1` from the lab results.

<details><summary><b>P1</b>: 5 cases</summary>

- **SEC-DEC.Tests.ps1** (1): SEC-DEC-06
- **T-DEV.Tests.ps1** (4): T-DEV-03, T-DEV-04, T-DEV-08, T-DEV-09

</details>

<details><summary><b>P2</b>: 4 cases</summary>

- **T-DEV.Tests.ps1** (4): T-DEV-01, T-DEV-02, T-DEV-05, T-DEV-07

</details>

<details><summary><b>P3</b>: 1 cases</summary>

- **T-DEV.Tests.ps1** (1): T-DEV-06

</details>

### Code vs sheet: notes collected from the cases

These are the places where the product code behaves differently from what the sheet row says, or where the lab still lacks something (a probe, OCR, console mouse, a second VM) to run the case. The full note is in the case. Each one needs a decision: fix the product, or fix the sheet row.

<details><summary>

| ID | Note |
|---|---|
| A-INST-16 | `ParseGroups` maps only the English word "Administrators" (any case) to `S-1-5-32-544`. A typed "Administratoren" is stored as typed (a name), not as the SID. |
| A-INST-25 | the service is `Vital="yes"` with `Start="install"`, so a service that doesn't start fails the whole install (rollback). Expect the "install fails" branch. |
| A-HAND-06 | the MSI has no custom dialog and no virtual-display checkbox (only the `VIRTUALDISPLAY` property); the UI is the standard Windows Installer progress UI. The checkbox exists only in RemoteHub's install dialog. |
| A-UPDFAIL-01 | the broken package has the version of the build under test (newer than the installed one), not the installed version. The verdict takes about 4.5 min (90 s down window plus 3 min up window). |
| A-UPDFAIL-03 | `WaitForRestartAsync` waits up to 90 s for the agent to go down and then up to 3 min for it to come back. The time limit is 3 min after the agent went away, not 3 min in total. Because the service is Vital, a service that doesn't start rolls the upgrade back, so the old agent may come back. The verdict may then be `notInstalled`, not `noAnswer`. |
| A-UPDFAIL-04 | in the current code `AgentInstaller.PackageVersion` returns null for an unreadable file. With no expected version, `WaitForRestartAsync` returns the first answer after the 90 s window. Both `MainWindow.RunAgentInstall` and the harness then report "Updated – RemoteHub Agent <old> is running.", so this test will probably fail until RemoteHub treats a package without a readable version as failed. The sheet's "reported as failed" is not checked as such; the test only asserts "not updated". |
| A-CFG-04 | `AgentLog.Clean` deletes by file LastWriteTime, not by the date in the name. Faked names alone are not enough; the test must set LastWriteTime. |
| D-ASK-01 | the consent, PIN and banner windows are custom-drawn Win32 popups of the SYSTEM helper. UI Automation sees only the window (title, bounds), not the text, the PIN or the buttons, and UIPI blocks clicks sent from a task running as normaluser. Text and PIN need OCR on the console screenshot; buttons need a click at their position via the console mouse (Hyper-V `Msvm_SyntheticMouse`, not in the lab spec yet); Esc works through the console keyboard. The heading shows the full account (`<VM>\PSAdmin`), not just "PSAdmin". |
| D-ASK-03 | the dialog's own countdown answers "deny", so the Status text is "The user at the device didn't allow it." "No answer in time." comes only from the service's fallback at ConsentTimeoutSeconds + 10 s, when the helper sends no answer at all. |
| D-PIN-01 | the lab spec says the PIN is read by AutomationId; the code has no UIA element for it (painted text), so only OCR works. |
| D-NONE-04 | the sheet expects event 1102 "own account". For a Start on the own session the code writes 1100 with "consent None"; 1102 "… signed in … with their own account – continued without asking" is written only when the admin signs in with the own account during a running session (D-SIGNIN-02, D-SAS-07). |
| D-BAN-01 | UIA does not expose the text or the Disconnect button (see D-ASK-01 note); the checks are window bounds/style plus OCR. |
| D-VIEW-03 | view-only can't be switched on during a session (it is fixed at Start, and the service drops all keys in view-only). The code only lets the release of a held key through when input becomes not allowed on the current desktop (secure desktop without the right, or a held user desktop); the case is written for that. |
| D-LOCK-01 | on Windows 11 / Server 2025 the "Press Ctrl+Alt+Delete" lock screen runs on the Default desktop; the helper then sends no frames until Ctrl+Alt+Del brings up the sign-in screen (D-LOCK-05). On those builds "lock screen in the picture" holds only after Ctrl+Alt+Del. |
| D-SIGNIN-02 | "no consent" applies only when the account that signs in is the one connected to the agent. If the harness connects as PSAdmin and types Admin's password, Admin is asked. |
| D-DISC-01 | for the console session `tsdiscon` moves the console to a new session; the service then follows the console (sign-in screen) right after the Paused status. The Paused/"has a screen again" pair is only stable for an RDP session. |
| D-SAS-04 | the agent writes the `sas.agent` marker at start and then treats the value as its own; with the marker present `Ensure()` sets the value back to 1 on the next Ctrl+Alt+Del. The test must delete the marker (or set the value before installing). |
| D-VID-08 | there is no hook to force a GPU encoder failure; the software fallback can only be checked by switching to ≤ 30 fps or on a GPU whose encoder fails by itself. |
| D-VID-09 | the sheet expects event 3100 and a clear error in RemoteHub. The code ends the helper with that text in the agent log only; RemoteHub gets the generic "kept failing" text and no 3100 event is written. |
| D-FIT-03 | the service resends the Fit settings to the restarted helper, so while connected the resolution is fitted again a few seconds after the kill. |
| D-MON-01 | with "always" the virtual monitor is only requested when a Fit size is sent (or the picture stays black); a plain Start does not switch it on. |
| D-VD-01 | "auto" asks for the virtual monitor only when the display's largest offered mode is smaller than the window (capped at 1280×720), or when the picture stays black. A Hyper-V display set to 640×480 still offers larger modes, so the condition is not met by the resolution alone. |
| D-CLIP-04 | the sheet says both arrive as PNG + CF_DIB and the device always sends PNG. The code puts a BMP only as CF_DIB, and sends BMP when the copying program offers no "PNG" format. |
| D-CLIP-08 | the text from the device differs from the sheet's "Too much at once". |
| D-CLIP-11 | a short transfer gets no "incomplete" message; it stays open until cancelled or the connection ends, then its folder is removed. "The copied items are incomplete." is only for items without a path. |
| D-RES-04 | the harness must drop the connection without a WebSocket close frame; a normal close counts as a deliberate end and voids the token at once. |
| D-RES-07 | the sheet expects A to end with "continued in a new connection". The code keeps that only as an internal end reason: A gets no final Status, and the 1101 event (where the reason would be logged) is skipped for a taken-over connection. |
| D-ROB-05 | a Start with broken JSON goes to the general error path: Status `Failed` "The remote desktop failed on the device. Details are in the agent's log." and one event 3100 per attempt (not a crash loop, but 3100 does appear). Malformed Mouse/Key messages are passed to the helper unchecked; the helper then ends and is restarted ("reconnecting…"), counting towards "kept failing". |
| C-MSI-05 | the MSI has the same upgrade code, but RDP Manager 2.x versions are higher than 0.x and `MajorUpgrade` refuses a downgrade ("A newer version of RemoteHub is already installed."). The README says to uninstall 2.x first. A direct upgrade only works from an RDP Manager build numbered below the RemoteHub version. The folder copy happens on the first start (`SettingsStore.MigrateFolder`), only if `%APPDATA%\RemoteHub` doesn't exist yet. |
| C-POL-07 | only `SettingsFolder` is read from the legacy `Policies\RdpManager` key (`SettingsStore.PolicyFolder`). All other policies (`AppPolicy`: telemetry, updates, channel, recording, audit retention) read only `Policies\RemoteHub`, HKLM first, then HKCU. |
| C-UPD-03 | only `remotehub.dev` (and subdomains) or release assets of `GrischaE1/RemoteHub` on `github.com` / `api.github.com` are allowed, over HTTPS. Other GitHub paths are refused too. After redirects the final host must be `github.com`, `githubusercontent.com` or `remotehub.dev`. |
| C-CRASH-01 | RemoteHub has no built-in switch to force a crash; the lab needs its own trigger (e.g. a test build hook). An "unexpected error" that RemoteHub survives ("RemoteHub – unexpected error" message box) is recorded the same way, at most three per session. |
| R-RDP-03 | the sheet expected Windows to refuse the stored password without the NTLM delegation policy. It doesn't: RemoteHub hands the password to the RDP control (`ClearTextPassword`), which Windows doesn't treat as saved in Credential Manager, so the tab signs in. Were it refused, the tab would show Windows' own text (`GetErrorDescription`); the README explains the policy. |
| R-RDP-08 | the password in the `.rdp` is DPAPI-protected for the exporting Windows user (like mstsc), so `normaluser` can't read it and Windows asks for it. The risks dialog is shown only when a `.rdp` file is opened (argument / file association) and creates a new entry, not for import through *New* or drag and drop. |
| UI-AG-02 | *Test RemoteHub Agent* is only in the context menu's *Tools* group while the connection uses the agent and the last result isn't "available"; after a successful test the group shows the tools instead. |
| UI-AD-07 | view-only is chosen on the connect page before connecting; there is no toggle while connected. |
| C-REC-01 | the agent desktop tab is called "Desktop (agent)" in the file name. |

</details>

### Likely product bugs found while writing the cases

| Case | Finding |
|---|---|
| A-UPDFAIL-04 | An update with an unreadable (truncated) MSI: `PackageVersion` comes back null, so RemoteHub reports "Updated – RemoteHub Agent <old version>" instead of a failure. The test is expected to fail until this is fixed. |
| A-UPDFAIL-02 | Seen in lab runs 20261012-214723 (W11) and 20261012-231526 (both VMs). An upgrade from 0.17.2 to 0.18.0 whose enrollment step fails ends with MSI 1603, and the device is left with **no agent**: the product is not registered, the install folder and service are gone, but `agent.json`, the certificate and the firewall rule are still there. The MSI rollback should have restored 0.17.2. The test now saves the whole verbose MSI log so the rollback can be traced. Traced in lab run 20261019-193627 (0.20.2 → 0.20.3): `RemoveExistingProducts` ran right after `InstallValidate` (WiX's default schedule), so the old version's removal was its own, already committed transaction; the rollback after the failed `AgentEnroll` only undid the new version. **Fixed** on branch credential-provider-by-default: `MajorUpgrade Schedule="afterInstallInitialize"` (`Package.wxs`), the removal is part of the upgrade's transaction and rolls back with it. |
| A-SAME-02 | Seen in lab runs 20261012-214723 and 20261012-231526 on both VMs. Installing the same version again with "Create a new device certificate": `agent.json` points to the new certificate, but for over 3 minutes the agent keeps answering with the old certificate and never with the new one. RemoteHub reports "didn't answer again". The service seems to keep running without picking up the new identity. Still there in 0.18.15 (lab run 20261016-212514): now `agent.json` also keeps the old device ID and certificate, and the agent log has no "Enrolled" or "starting" line after the install – the same-version install seems to skip the enrollment altogether. Cause: the very same package (same product code) opened maintenance mode – nothing was reinstalled, and `AgentEnroll` only ran `NOT Installed`. **Fixed** on branch credential-provider-by-default: when the device has the package's product code, RemoteHub's install scripts add `REINSTALL=ALL REINSTALLMODE=vamus` (the service restarts), and `AgentEnroll` also runs on a reinstall with an `ENROLLMENT` given on the command line (`ENROLLMENTGIVEN`, `Package.wxs`). |
| W-* (note) | On Windows clients the default execution policy is Restricted, so the generated setup script can't run as a .ps1 file until the admin allows scripts for that window (`Set-ExecutionPolicy -Scope Process Bypass`). The lab does that now. Consider adding that line to the instructions RemoteHub shows. |
| S-AUTH-08 | Windows error 1907 (password must be changed) has no mapping in the agent's sign-in code and comes back as HTTP 500 instead of a clear refusal. **Fixed** on branch fixes-20261017 (`6dd3686`): 1907 and the other refusals get 401 or 403 with the reason; S-AUTH-08 and -10 pass in lab run 20261017-160133 (0.18.16, W11 and Server 2025). |
| T-FS-09 | A cancelled upload can leave a `.remotehub-part` file on the device. |
| W-* / P-SES | `ParseGroups` only recognises the English group name "Administrators", so on a de-DE device ("Administratoren") the admin check fails. |
| D-VID-09 | When the Media Foundation H.264 encoder is missing (N edition without the Media Feature Pack), the client only gets the generic "kept failing" text and no event 3100 is written. The real cause appears only in the agent log. |
| D-RES-07 | A connection that is taken over gets no final Status, and its 1101 audit event is skipped, so the audit trail doesn't show the hand-over. |
| S-AUTH-08 / S-AUTH-10 | Seen in lab run 20261014-214238 (W11). An expired account (Windows error 1793) and an account that must change its password (1907) both get HTTP 500, not 401. Same unmapped-error path as above. **Fixed** on branch fixes-20261017 (`6dd3686`), passing in lab run 20261017-160133 (0.18.16, both platforms). |
| A-INST-25 | Another program listening on 58732 without exclusive use: the agent binds the same port as well, without a warning. Connections then go to one of the two at random. Still so in lab run 20261019-193627 (0.20.3, both platforms). **Fixed** on branch credential-provider-by-default: before binding, the agent checks whether anything listens on the port (any address, IPv4 or IPv6) and fails to start ("could not start: … address already in use"), so the install rolls back; unit test `The_agent_does_not_start_on_a_port_another_program_listens_on`. That check sat in the bind, after the service had reported running, so the install still succeeded (lab run 20261021-062735, 0.21.2): it now runs before the service starts (`Program.cs`), the start fails and the install rolls back. |
| A-ROLLBACK-01 | With 58732 held exclusively by another program, the service reports "started", then fails to bind. The MSI and RemoteHub report a successful install, but the agent isn't running. **Fixed** with A-INST-25 above: the port is checked before the service starts. |
| P-SES-06 | Seen in lab run 20261014-214238. A network drop ends the PowerShell session in time (54 s), but event 1001 gives the reason "PowerShell ended", not "connection lost: …". The keep-alive timeout ends the shell first, and `Tunnel.RunAsync` then reports the shell side (`AgentHost.cs:328`). **Fixed** on branch fixes-20261017 (`f7e9981`), passing in lab run 20261017-160133 (0.18.16). |
| UI-* (accessibility) | Icon-only buttons in the PowerShell tab (`PsConsoleView.HeaderButton`, e.g. "Stop the running command") have only a tooltip and no `AutomationProperties.Name`, unlike `RemoteToolChrome.Tool`. The terminal's input line and output have no name either. |
| C-REC-06 | Code review (0.18.11). On a Windows N edition without the Media Feature Pack `mfplat.dll` is missing: `MFStartup` throws `DllNotFoundException`, which `Mp4Writer.Create` doesn't catch (only `InvalidOperationException`), so the recording stops with "Unable to load DLL 'mfplat.dll' …" instead of the H.264 / Media Feature Pack message (`SessionRecorder.cs:349, 366, 387, 562`). |
| C-DB-13 / C-LOCK-09 | Code review (0.18.11). Two messages point to menu entries that don't exist: "You can restore an exported backup via Menu → Import." (`App.xaml.cs:94`) and "Export a backup first (Menu → Export backup)" (`StartupProtectionWindow.cs:118`). Import and export are under Settings → Security & storage, as the other database message already says (`App.xaml.cs:167`). |
| A-INST-28 | Code review (0.18.11). `AUTOSIGNIN=1` on an agent package built without the credential provider still records the install's choice to allow automatic sign-in (`agent.json` `noUserBehavior` / `autoSignInGroups` since 0.18.12, `NoUserBehavior` and `Rights\AutoSignIn` in the policy key before; "Automatic sign-in allowed on this device …"): `auto-sign-in=[AUTOSIGNIN]` sits outside the `CredentialProviderDll` condition in `Package.wxs:65`. |
| C-TEL-03 | Code review (0.18.11). Telemetry must be fully off under the `DisableTelemetry` policy, but only the Settings switch deletes `install-id` (C-TEL-02); with the policy it stays on disk (`Telemetry.cs:146-148`, `EditorWindow.xaml.cs:401`). Decided 2026-10-15: product bug. Confirmed in every lab run since 16 Oct. **Fixed:** with telemetry off, `AppStart` now deletes the install ID as well (`Telemetry.DiscardLocalRecords`); unit test `Telemetry_off_deletes_pending_crash_reports_and_the_install_id`. To be confirmed by a lab run. |
| C-END-05 / C-END-15 / D-RES-10 (also C-END-06, -07, -08, -12) | Seen in lab run 20261015-072527 (W11), root cause from the code. Confirmed in lab run 20261016-212514 (both W11 VMs, 0.18.15): C-END-06's adapter cut ended the connection after 27 s as "closed by RemoteHub" and locked the session; the harness' `drop` (socket aborted) did the same in 3 of 5 drops (C-END-07, -08, -12; C-END-05 and -11 got "connection lost" and the 2 minutes), so it is a race between the failed read and the request being aborted. A .NET 10 check shows the keep-alive timeout ends `ReceiveAsync` with `OperationCanceledException("Aborted")`, so the `Task.Run` around the client loop ends cancelled. When the agent notices a lost connection first (keep-alive 30 s, or TCP giving up on unacknowledged frames), the pending `ReceiveAsync` throws an `OperationCanceledException`, so the client loop ends *cancelled*, not *faulted*. `DesktopEndpoint.cs:220-230` only checks `IsFaulted`: `_lost` stays false, the reason stays "closed by RemoteHub", the resume token is revoked instead of kept, and a session automatic sign-in created gets its end action at once ("Automatic sign-in session 7 … locked (closed by RemoteHub)") instead of the cover for 2 minutes. Affects every agent desktop connection whose drop the agent notices first – short cuts only work because RemoteHub resumes before the agent notices. Fix: treat `fromClient.IsCanceled` (not on purpose) as lost. P-SES-06 is the same pattern in `AgentHost.cs:327-329`. **Fixed** on branch fixes-20261017 (`f7e9981`, with `bd9fd8b` for an aborted request and `7f0d9cf` so that a drop during a helper frame keeps the cover – C-END-12): C-END passes 8 of 8 on W11 and Server 2025 in lab runs 20261018-152145 (0.18.20) and 20261018-162105 (0.18.21; C-END-01 on W11 needed the marker fix below). |
| P-SES-06 | Seen in lab run 20261016-212514 (W11, 0.18.15): after a network drop the PowerShell session ended as "Session ended: … (PowerShell ended)" instead of "connection lost" (event 1001). Same pattern as D-RES-10: `Tunnel.RunAsync` (`AgentHost.cs:333-335`) only checks `first.IsFaulted`; a drop seen through the keep-alive ends the WebSocket task cancelled, not faulted, so the reason falls back to "closed by RemoteHub" / "PowerShell ended". Fix: treat a cancelled transfer task (when the agent didn't cancel it itself) as "connection lost", as in the desktop endpoint. **Fixed** on branch fixes-20261017 (`f7e9981`), passing in lab run 20261017-160133 (0.18.16). |
| UI-TL-03 | Seen in lab run 20261015-020456. The registry tab's *New* menu shows "String value (REGSZ)…", "(REGEXPAND_SZ)", "(REGMULTI_SZ)": WPF takes the first `_` of a `MenuItem` header as the access-key marker (`RegistryView.NewKinds` needs `__`). Also: a path typed into the address box isn't found when the key was created after the parent was loaded ("Key not found" until Refresh) – `Navigate` doesn't reload the cached children. |
| D-HIDE-03 | Code review (0.18.11). Apps hidden by the policy `HiddenApps` are counted as hidden by the user: the first Privacy message has hiddenApps=1, the banner says "1 app hidden" and event 1105 claims "the user … hides 1 app(s)" (`DesktopHelper.cs:177-179`, `DesktopEndpoint.cs:762-763`). **Fixed** on branch fixes-20261017 (`7903d72`; the Privacy message has a separate `policyHiddenApps`), passing in lab run 20261017-160133 (0.18.16). |
| D-HIDE-01 (privacy) | **Privacy issue.** Seen in lab run 20261016-212514 and the spike runs spike-ctl-W11-D-PRIV and spike-reg-W11-D-PRIV (W11 26H2, 0.18.15) – D-HIDE-01 has never passed on Windows 11: with `HiddenApps` set to the full path of the program that owns the window (`Get-Process notepad`: the Store Notepad's `Notepad.exe` under `C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_…`), Notepad and its secret text were fully in the picture (the frame identical to the control frame without the policy), and the Privacy message had `hiddenApps` 1 but no covered regions. Cause from the code: for a packaged process the helper names the app only by `aumid:` + its AppUserModelID and drops the program's path (`AppWindows.cs:198-205`), and `AppMask` compares only that name (`AppWindows.cs:329`), so a path never matches a packaged desktop app – the new Notepad, Paint, Windows Terminal and any vendor app shipped as MSIX. `C:\Windows\System32\notepad.exe`, what an admin would expect to work, is only a stub that starts the Store app and owns no window, so it can't match either. The ADMX help, README and `agent-spec.md` say "the full path of a program", nothing warns that the entry has no effect, and the failure is silent and open: the banner even says "1 app hidden". Fix: also hide a window when its program's path (`exe`, which `Identify` already has) is in the list – `AppWindow` needs the path next to `App` – and say in the ADMX help that a Store app's path changes with each update (its folder carries the version), so `aumid:` is the lasting entry; a unit test in `AppMask` tests for a packaged window listed by path. **Fixed** on branch fixes-20261017 (`9af6c7f`): a packaged app is hidden by its program's path too; D-HIDE-01 passes in lab run 20261017-160133 (0.18.16, W11). |
| C-END-14 | Code review (0.18.11). The cover the service puts up after an automatic sign-in is audited as 1104 "<admin> … turned the privacy screen on …" although the admin didn't (`DesktopEndpoint.cs:768-771`). |
| UI-CP-13 | Code review (0.18.11). With `DesktopClipboard=2` (text and images) a file copy is refused with "Copying images and files isn't allowed on this device – only text.", which is wrong for that level; the agent's own text is "Copying files isn't allowed on this device." (`AgentDesktopSession.cs:402-403`). Confirmed in lab run 20261016-212514 (Console admin PC, lane 2, Server 2025 device, 0.18.15): f1.bin copied with level 2 got "Copying images and files isn't allowed on this device – only text.". The image half of the case failed in that run only because of the test – its picture comparison was named `Diff`, which Windows PowerShell 5.1 resolves to the alias for `Compare-Object` (fixed: `Get-PicDiff`); the device did fetch the picture ("clipboard: pasted by powershell (4444) – … 1 item(s), 619 bytes"). Seen again in lab run 20261019-193627 (0.20.3). **Fixed** after that run: with images allowed RemoteHub says "Copying files isn't allowed on this device." (`AgentDesktopSession.RefusedCopyNotice`, unit test `A_refused_copy_names_only_what_the_level_refuses`). |
| UI-TL-09 | Seen in lab run 20261016-212514 (Console admin PC, lane 2, 0.18.15): the setup script copied with *Copy script* was still on the clipboard 156 s later (5956 characters), although the dialog says it is "cleared after 120 s". The dialog had been closed about 108 s after the copy. `SecureClipboard.CopySecret` keeps the copying window's handle as the clipboard owner – here the dialog's (`RemotingSetupDialog.cs:135-136`, `SecureClipboard.cs:25`) – and the timer's `ClearIfOurs` opens the clipboard with that handle (`SecureClipboard.cs:50`). Once the dialog is closed the handle is gone, `OpenClipboard` fails with error 1400 (invalid window handle – checked on Windows Server 2025), nothing is emptied and `_ourSequence` is reset (`:55`), so the clear at app exit (`MainWindow.xaml.cs:2508`) doesn't remove it either. Copying the script, closing the dialog and pasting it on the target – the usual order – leaves it on the clipboard for good. A password copied from the main window isn't affected (that window stays open). Fix: when `OpenClipboard(_owner)` fails because the window is gone (`IsWindow`), open the clipboard with no owner (`IntPtr.Zero` – `EmptyClipboard` still clears it) or with the main window; a unit test for a copy whose owner window is destroyed before the timeout. **Fixed** on branch fixes-20261017 (`f04d5ed`), passing in lab run 20261017-160133 (0.18.16). |
| C-AUTO-15 | Confirmed in lab runs 20261015-192844 (W11, 0.18.14) and 20261016-212514 (W11, 0.18.15): SignInFailed "The connection to the credential provider broke: Pipe is broken.". Code review (0.18.11): If someone signs in at the console while an automatic sign-in waits, the broker uses the LogonUI client it already holds (`WaitForClient`) and only re-checks after the provider's Claim (`AutoSignInBroker.cs:221`); that LogonUI is gone, so the result is NoProvider "The connection to the credential provider broke …" instead of SessionChanged "Someone is signed in on the console now". No ticket goes out, but reason and code are wrong. Fix: run the session check before calling the broker. **Fixed** on branch fixes-20261017 (`baa0025`), passing on W11 and Server 2025 in lab run 20261018-112637 (0.18.17). |
| C-AUTO-20 | Seen in lab runs 20261015-192844 (W11, 0.18.14) and 20261016-212514 (W11, 0.18.15): for a locked and for a disabled account no result reaches the agent at all – SignInFailed after the timeout "The sign-in screen didn't take the sign-in. …" instead of the reason (an expired password and "Deny log on locally" do get their own texts). Code review (0.18.11): Windows reports a disabled account to LogonUI as 0xC000006E with substatus 0xC0000072; `AutoSignInStatus.Describe` (`AutoSignIn.cs:211`) matches 0xC000006E first and says "can't sign in this way (it may need a smart card …)". Only the 0xC0000064 substatus is handled; an expired account (0xC0000193) is likely affected the same way. **Fixed** on branch fixes-20261017: the substatus is read first (`baa0025`); for accounts Windows 11 leaves off the sign-in screen the provider reports "not offered" after 10 s and the agent asks Windows itself (`732254b`, credential-provider-design.md §8.6). Lab run 20261018-162105 (0.18.21): all four refusals give Windows' reason on W11 and on Server 2025 – rhaslock 1909, rhasdisabled 1331, rhasexpired 1907, rhasdeny 1385 ("checked with Windows: error N" in 2105). |
| C-END-10 (result lost) | **Security issue.** Seen once, in lab run 20261016-212514 (Server 2025, lane 2, 0.18.15); the policy level (`PrivacyScreen=0`) plays no part – nothing on the sign-in path reads it before the result. The agent reported SignInFailed "The sign-in screen didn't take the sign-in. …" (logged as "Automatic sign-in refused … (timeout)") only 3.9 s after the start, and 0.5 s later its helper saw Admin's desktop: Windows had signed the account in (session 3 Active with explorer.exe). Every other "(timeout)" in the lab's agent logs comes after the full 30 s; this one is the pipe ending early (`Read` returns null on 0 bytes, `AutoSignInBroker.cs:237-243`). Cause from the code: after a successful logon LogonUI calls `ReportResult` and then releases the provider, whose destructor calls `Channel::Stop` (`Provider.cpp:48-50`). The channel thread waits on `{ stop, readDone, ResultEvent }` (`Channel.cpp:367-369`); when it wakes with both stop and the result signalled, `WaitForMultipleObjects` returns the lowest index – stop – so `Serve` returns without writing the result and the pipe closes. A busier VM makes the race likelier. The consequence is worse than a wrong message: the agent doesn't record the session as one automatic sign-in created, so it gets no cover (at levels 1 and 2 the console would show the admin's desktop), no end action (closing the connection left it signed in and unlocked – no 1107) and the ordinary banner ("Controlled by … Disconnect"). Fix: send a pending result before honouring stop (check `ResultPending` when stop wins, or put the result event first), and on the agent side treat "pipe ended after the ticket went out" while the console session now has the ticket's account signed in as success, or at least apply the end action to it. C-END2 now says in C-END-10's message whether the account was signed in anyway. Seen a second time in the same run, in isolation: C-PROV-03 on Server 2025 lane 2 (Lab provider build + RemoteHubCpTestServer.exe) logged "sent Ticket" at 18:23:31.890 and then "error no valid Result" 120 s later – the provider took the ticket and never sent its result. A third time in rerun 20261017-054704: C-PROV-03 on Server 2025 lane 1 - "sent Ticket" at 00:01:59.805 (guest time), "error no valid Result" 120 s later. So far: 2 of 3 C-PROV-03 runs on Server 2025 and 0 of 2 on W11 lost the result - not rare on Server. **Fixed** on branch fixes-20261017 (`baa0025`): the channel sends a pending result before it honours stop. C-PROV passes 8 of 8 on both platforms in lab runs 20261018-152145 (0.18.20) and 20261018-162105 (0.18.21). |
| C-END-01 (marker) | To confirm – seen once, in lab run 20261016-212514 (Server 2025, lane 1, 0.18.15): the sign-in and the cover were fine, but none of RemoteHub's keys reached the marker window, and the frame RemoteHub took 3 s after the keys shows the plain desktop – the marker (a full-screen, topmost magenta window) wasn't there at all, while on W11 it was. The wait before the keys was 99 s instead of 57 s, which fits `Start-LabMarker` giving up after 60 s without the window showing; it then carried on silently, so C-END-01's "local keys blocked" check passed for nothing. The same helper did work on Server in C-END2's uncovered session (5 local keys), so the open question is whether a marker started in a session under the service's cover (input blocked) comes up on Server 2025. `Start-LabMarker` now returns `Shown` and `Why` (the task's state and last result, whether its PowerShell is running), and C-END-01, C-END-09 and C-END-12 fail with that text when the marker never came up, instead of counting keys that couldn't arrive. **Lab, fixed on 18 Oct.** Seen again in lab run 20261018-162105 (W11, 0.18.21): the marker was shown and RemoteHub's picture had it, but none of the 6 keys arrived – the window was up without the keyboard (a process started by a task may be refused the foreground). The marker now takes the foreground itself and records `active` / `inactive` (with the window that took it) and `nofocus`; `Start-LabMarker` waits for `active` and otherwise says which window kept the keyboard. C-END-01 passes on both platforms in lab run 20261018-171338 (6 of 6 keys). |
| C-END-13 | Seen in lab run 20261015-225719 (W11, 0.18.14). After a failed automatic sign-in (wrong password) the agent reports SignInFailed correctly, but RemoteHub's tile on the sign-in screen stays selected and still says `Signing in DESKTOP-GGPVP10\Admin…`. The `Automatic sign-in failed` text the credential provider has for this (Credential.cpp:91) never shows, and there is no password box, so the person at the device has to pick the password under *Sign-in options* to sign in by hand. The tile's text is probably never refreshed (no `SetFieldString` through the credential events after the failure). The test now goes through *Sign-in options*. |
| C-END-13 (text) | Seen in the same run: the tile shows `Signing in DESKTOP-GGPVP10\Adminâ€¦` instead of `…`. The literal `L"…"` in Credential.cpp:94 is in a UTF-8 file without a BOM, and the project builds without `/utf-8`, so MSVC reads it as cp1252. Fix: `L"\u2026"` or `/utf-8` for the credential provider project. |
| UI-JUMP (tree) | Seen in lab run 20261016-024318 (W11, 0.18.14): a connection or folder created with New, or made with Duplicate, below the visible rows of the connection tree is selected but not scrolled into view - the old row keeps its highlight and the new item is out of sight. NewConnection_Click / NewFolder_Click (MainWindow.xaml.cs:1873, 1884) and CmDuplicate_Click (:1763) only set IsSelected on the virtualized tree (MainWindow.xaml:445-458); RevealInTree (:608-638) already scrolls an item into view and could be called there. The lab's UI driver now scrolls an off-screen element into view itself, so the tests don't depend on it. Seen again in lab run 20261016-212514 (Console 26H2 admin PC, 0.18.15): UI-JUMP's tree step stopped at "'Orphan' … not found" right after New connection had created it below the visible rows – the virtualized tree had built no row for it, so neither UI Automation nor the driver's scrolling could reach it. It caused none of that run's failed cases: those came from rows scrolled partly above the tree's top, which UI Automation still counts as on the screen, so right-clicks and drops landed on the search box; UI-JUMP now scrolls such rows into view first. |
| R-RDP (code 263) | Minor, seen in lab runs 20261015-072527 and 20261016-024318: when Windows' question about an untrusted RDP server certificate is dismissed (Esc or No), the tab only says "Disconnected (code 263)." - RdpSession.OnDisconnected (RdpSession.cs:309-331) falls back to the bare code when GetErrorDescription returns nothing. A plain text such as "The server's certificate was not accepted." would tell the admin what happened. (The lab's RDP tests set "Connect without warning" for the lab's self-signed device certificates.) |
| R-RDP-07 (full screen keeps the tab's size) | Seen in lab run 20261017-054704 (Console 26H2 admin PC, Server 2025 device, 0.18.15): after *Full screen* the RDP control's own full-screen window covered the 1024 × 768 screen, but the session stayed at the tab's 460 × 492 pixels, centred on black (`ui-rdp-fullscreen.png`, 2 s after the click). Dynamic resolution is switched off while the control is in full screen: `ApplyDynamicResolution` returns at once when `IsFullScreen` (`RdpSession.cs:301`) and `Fits` counts full screen as fitting (`:285`), so nothing asks the server for the screen's size, and leaving full screen doesn't start a fit either. Fix: on `OnEnterFullScreenMode` send the full-screen size (`UpdateSessionDisplaySettings` with the monitor's size) and fit again on `OnLeaveFullScreenMode`. R-RDP-07 doesn't check the size. To confirm, same run: right after leaving full screen with Ctrl+Alt+Break, *Detach into its own window / dock back* (UI Automation's Invoke) opened no window within 30 s, although it worked in earlier runs where full screen hadn't come up; nothing was recorded then, UI-RDP now keeps RemoteHub's state and tries a real click as well (`ui-rdp-detach-invoke.json`, `detClick` / `detRealClick` in `ui-rdp-tab.json`). |
| C-REC-01/05 (duration) | Seen in lab run 20261016-135543 (W11, 0.18.14): recordings are written as fragmented MP4 (SessionRecorder.cs:349 Mp4Writer fragmented, :375 MFTranscodeContainerType_FMPEG4) without an mehd box and with about one fragment per 3 frames. The movie header and the frames agree with RemoteHub's "Recording saved" length (e.g. 15.8 s / 158 frames, 30.1 s), but Windows' media pipeline (MediaSource, as players use it, and MediaClip) reports whole multiples of 10 s less (10 s, 10 s). Players will likely show a wrong length and seek bar. Writing an mehd (fragment duration) or finalizing as a plain MP4 at stop would fix it. C-REC-05 now checks the frames' length (fragSeconds) as well. |
| S-SMB-08 (folder ACL) | Minor, seen in lab run 20261016-135543 (Server device): a share whose folder ACL (not the share permission) allows only Administrators is connected without complaint, and the refusal comes at the first listing as the bare "Access to the path '\\ip\share' is denied." without the remote UAC hint. WindowsSmbShare's constructor explains it only when the share root can't be seen (WindowsSmbShare.cs:35-45); here Directory.Exists is true, and List() (:68) goes through Io() (:111-118) with the bare message. The share-permission variant gets the hint as designed. |
| UI-CP-08 | Seen in lab run 20261016-135543 (W11 admin PC, Server device, 0.18.14): with the device's session locked, clipboard text still went both ways through the agent tab - the admin's text arrived on the device (its SHA-256 equal) and the device's text on the admin's PC. The agent passes clipboard messages gated only on the connection being live and the clipboard level (RemoteHub.Agent\Desktop\DesktopEndpoint.cs:677, :953; DesktopHelper.cs:165, :240); locking switches to the secure desktop and back within a second without restarting the helper. The case expects nothing to or from the device's clipboard while it is locked. Again in lab run 20261016-212514 (Console admin PC, Server 2025 device, 0.18.15), both ways: the admin's "while locked …" text was on the device's clipboard and the device's "device locked …" text on the admin's PC. The agent log shows why the gate never closes: `Lock-LabSession` at 16:27:14 "switched to the secure desktop", 16:27:15 "switched to the user's desktop" – on Server 2025 / Windows 11 the lock screen runs on the Default desktop (D-LOCK-05), so the helper treats the locked session as the user's desktop until Ctrl+Alt+Del. A lock check has to look at the session's lock state (`WTSSESSION_LOCK` / `WTSQuerySessionInformation` `SessionFlags`), not only the input desktop. Seen again in lab run 20261019-193627 (agent 0.20.3). **Fixed after that run:** the agent's service closes the clipboard both ways while LogonUI runs in the session (`WtsNative.IsLocked`, the check the session list uses – the WTS lock flag is unreliable): text is dropped, a copy offered or asked for is refused with "Nothing is copied while the device shows its lock or sign-in screen – copy again once it is unlocked." (`DesktopEndpoint.ClipboardClosed`; xUnit `Nothing_goes_through_the_clipboard_while_the_lock_screen_is_up`). |
| C-AUTO-16 | Code review (0.18.11). `CheckInstallation` ignores the provider key's `Disabled` value: Hello offers automatic sign-in and the operator waits about 23 s for "The device's sign-in screen didn't load the RemoteHub credential provider." |
| S-SMB-08 | Code review (0.18.11). The remote-UAC hint for local administrators is only added when listing the share fails; access denied by the share's own permissions fails earlier in `WindowsSmbShare.Acquire` (`WNetAddConnection2`, error 5) and shows "\\host\share: Access is denied." without the hint. |
| UI-AG-12 | Code review (0.18.11). The notice "Automatic sign-in needs a stored password for this connection …" can't be reached: without a stored password RemoteHub asks for one first (`BuildTarget`, `PasswordDialog` refuses an empty one), and a typed password works as well – the text should not say "stored". A password over 256 characters can't sign in to the agent either. |
| D-VD-05 (virtual display after a restart) | Seen in lab runs 20261015-192844, 20261015-213401, 20261016-024318 (W11), 20261016-135543 (W11 and Server 2025) and 20261016-212514 (W11 lane 2), 0.18.14 and 0.18.15. Once the device has restarted after the agent was installed (in the lab: the auto logon's restart), the virtual display can't be switched on any more: "virtual monitor not switched on: The virtual display couldn't be switched on (pnputil exit code 1167: Failed to enable device: ROOT\DISPLAY\0000 The device is not connected.)", and `pnputil /enum-devices` shows the device as *Disconnected* (Get-PnpDevice: Error). Every switch-on that worked (C-AUTO3, D-MORE, run 20261016-212514) came after an install without a restart in between, every one after a restart failed. The same run (W11 lane 1) shows it in D-VIDEO: D-MON-01 switched the virtual monitor on (no restart yet); the virtual display context then powers the VM off and on to limit the Hyper-V display to 1024×768, and from there the device stayed *Error* and every Fit went to the Hyper-V adapter ("1600×900 asked → 1024×768 (closest listed mode); display modes: 1024×768, 800×600, 640×480"). Affected cases: D-VD-05 (D-MORE2), D-VD-01, D-VD-03 and D-VD-04 (D-VIDEO) – they fail until this is fixed. `Show` only falls back to the driver's pipe when the device is already started (`VirtualDisplay.cs:170-171`) and otherwise gives up (`SetEnabled`, :205-209), so on a real headless server the virtual display works only until its first reboot. Fix to try: when enabling fails, bring the device back the way `Install` does – remove the device node and create it again (`CreateRootDevice` + `UpdateDriver`, :94-95) – or rescan (`pnputil /scan-devices`) and enable again; or keep the device enabled and switch the monitor off through the driver's settings instead of disabling it. **Fixed** on branch fixes-20261017: rescan, then re-create the device (`89c4d1f`); switched off with the last connection also after a re-create (`ece37ee`). D-VD-05 passes in lab runs 20261018-152145 and 20261018-162105; D-VD-01 to 04 pass on Server 2025 in 20261018-162105 and on W11 in 20261018-171338 – see "D-VD-01 (slow switch-off)" for the time it takes on W11. |
| D-VD-01 (slow switch-off) | Seen in lab run 20261018-171338 (W11 lane 2, 0.18.21): after the device had been created again, the switch-off at the last connection's end took 65 s – the first two `pnputil /disable-device` calls didn't answer within their 20 s and were stopped ("still on after try 1 (pnputil exit code 1460: pnputil didn't answer within 20 s and was stopped)"), the third switched it off. On Server 2025 it took 7 s (run 20261018-171338) and the first try worked. In run 20261018-162105 the test read the device 5 s after the end and the agent was restarted 9 s after it, so the switch-off was cut off and D-VD-01 to 03 failed on W11; D-VIDEO now waits up to 120 s for the agent's line. Likely cause, to check: the virtual monitor is still the primary monitor of the sign-in screen when it is disabled. Fix to try: make the other display primary again (or detach the virtual monitor) before disabling the device. |
| D-VID-05 (quality on a slow link) | Confirmed in lab run 20261017-054704 (W11 lane 2, 0.18.15): with the noise window the 2 Mbit/s link was full (~1892 kbit/s of video arrived, 46 frames, 1-2 fps), yet the quality stayed at 80 in every sample. First seen in code review; earlier, in run 20261016-212514 the test didn't load the link (pointer moves only – 391 KB of video in 40 s), so the quality stayed at 80 for lack of traffic; D-VIDEO now sends a window of random pixels. The quality steps down only when `blocked > loops` within 2 s (`DesktopHelper.cs:966`). On a saturated link each wait for an ack (`blocked++`, :805) is followed by exactly one capture (`loops++`, :809) that sends the next frame and fills the window again, so `blocked == loops` and the quality never steps down; `blocked > loops` only happens when stale semaphore counts make a wait return early – fast acks, the opposite case. If an ack takes over a second, the wait gives up and the two sends after it make `loops` larger still. Also, the next frame put the viewer's quality back (`quality != _quality` compared the encoder's lowered quality with the one asked for). Seen again in run 20261019-193627 (80 in every stat, Server 2025 and W11). Fixed: the agent steps down when a quarter or more of the 2 s goes to waiting for acks and applies only a new quality from the viewer (`DesktopHelperSession.AdaptedQuality`; tests `The_quality_follows_the_time_spent_waiting_for_acks`, `The_quality_steps_down_while_the_acks_come_late_and_stays_down`). |
| UI-AG-07 | Code review (0.18.11). The install dialog still says "Anyone at the device can see the session while you work" next to "Allow automatic sign-in"; with the cover after an automatic sign-in that is no longer true in most cases. |
| D-* (accessibility) | The agent's consent, PIN and banner windows (`RemoteHubAgentWindow`) expose no UI Automation elements, so screen readers can't read them. The lab reads them by OCR. |
| S-* (audit) | IPv4 callers are logged as IPv4-mapped IPv6 (`::ffff:192.168.250.25`) in the events and the log. |
| D-ROB-05 | Malformed Mouse/Key messages go to the helper unchecked. The helper crashes, gets restarted, and the crashes count towards "kept failing". |
| A-REPAIR-01 | Minor, seen in lab runs 20261016-200451, -205935 and -212514 (W11, 0.18.14/0.18.15): `msiexec /fa <product code> /qn` ends with 1603 – the service is stopped, started again 3 s later by the rollback, and nothing changes. The verbose log wasn't kept then; the cause from the code: `AgentInstaller` installs from a temp folder it deletes afterwards (`RemoveFolderScript`, `AgentInstaller.cs:138, 216`), and the cached package has no files (`MediaTemplate EmbedCab="yes"`, `Package.wxs:23`), so a full repair finds no source (normally error 1706). Windows still offers *Repair* in Programs and Features (no `ARPNOREPAIR`); it can only work when the admin points it at the MSI. Fix: set `ARPNOREPAIR=1` next to `ARPNOMODIFY` (RemoteHub's install again, A-SAME-01, is the supported repair), or keep a copy of the MSI on the device. The test now repairs from the MSI and saves the log. Confirmed in runs 20261017-054704 and 20261017-093048: `/fa` with a copy of the MSI - even under the registered package name `RemoteHub-Agent-x64.msi` - still ends with error 1706 ("SOURCEMGMT: Failed to resolve source"): Windows Installer only looks in the registered source list, which holds RemoteHub's deleted upload folder. So a repair can't work even when an admin has the MSI at hand; the fix stays `ARPNOREPAIR` (no Repair offered) or registering a source that stays on the device. Again in lab run 20261019-193627 (1706, "SOURCEMGMT: Failed to resolve source"). **Fixed** on branch credential-provider-by-default: `ARPNOREPAIR=1`; the test now repairs the way that works with the MSI at hand, a maintenance install launched from it (`/i … REINSTALL=ALL`). |
| C-SHARE-02 | Seen in lab run 20261016-212514 (Console VM with the Server as the second PC, 0.18.15; run 20261016-024318 on 0.18.14 also got no conflict question in the first run): the first edit after *Open shared settings* (an existing shared file) is never written to it. The device's RemoteHub renamed ConnB and saved, and the shared file stayed unchanged (same SHA-256 8 s later, and A's save then met no conflict); the second run's save, after a reload, was written within seconds. Cause from the code: `SettingsStore.OpenShared` (`SettingsStore.cs:529-538`) loads the file but doesn't call `SetBaseline`, so the change tracker still belongs to this PC's database. The first `Save` then sees `_baselineOf != Location` and resets the tracker to the data as it is now – the edit included (`SettingsStore.cs:219-224`) – so the diff is empty and `Save` returns without writing (`:228`). The edit is lost unless something else changes later, also on exit; the other PCs never see it. **Fixed:** `OpenShared` now calls `SetBaseline(data)` as `Load` and `Reload` do; unit test `First_edit_after_opening_an_existing_shared_file_is_written`. To be confirmed by a lab run. |
| D-CLIP-06 / -08 / -10 / -12 / -16 (also D-CLIP-21, UI-CP-05) | Seen in lab run 20261016-212514 (W11, 0.18.15), in the spike runs spike-ctl-W11-D-CLIP and spike-reg-W11-D-CLIP, and in D-CLIP-21 (run 20261016-024318) and UI-CP-05 (run 20261016-135543): files copied at the device in normaluser's session (`Set-Clipboard -Path`, Windows Forms `SetFileDropList`) never reach RemoteHub. No TransferOffer, no TransferEnd with a refusal (not even "Too much to copy …" for a 101 MB file), no helper restart and no line in the agent log. Text and images copied the same way in the same sessions do arrive (D-CLIP-02, the image offer of D-CLIP-04). The code path (`Win32HelperUi.OnClipboard` → `ReadOther`, CF_HDROP → `DesktopHelperSession.SendClipboard` → `ClipboardTransfer.Files` as the user → TransferOffer) shows no gap, and the same reading of CF_HDROP works in an ordinary process on the lab host. The cause isn't settled yet. D-CLIP now keeps the output of each `Set-Clipboard` and the device's clipboard formats after it, so the next run shows whether the files were on the clipboard at all. UI-CP-05 again in lab run 20261016-212514 (Console admin PC, Server 2025 device, 0.18.15): after `SetFileDropList` in normaluser's session nothing came – no notice in the tab, no line in the agent log – and the admin's clipboard still held UI-CP-04's own file list (`FileDrop`), so the paste on the admin's PC brought back f1.bin … inner2.bin instead of a.txt … d.txt. **Fixed** on branch fixes-20261017 (`eb5da1a`, `8a266c0`, and `1bedd10`: the LocalSystem helper was shown only Explorer's "DataObject" for a file copy, so the list is now read as the signed-in user). D-CLIP passes 18 of 18 on W11 and Server 2025 in lab run 20261018-162105 (0.18.21), D-CLIP-16 included. |
| D-CLIP-05 / -07 / -17 (also D-CLIP-19, UI-CP-04, UI-CP-15) | **A lab paste problem (no message pump during the shell's paste; fixed in the lab on 17 Oct).** Confirmed in lab run 20261017-093048 (W11, 0.18.15): with `Invoke-LabPaste` D-CLIP-05 (3 files and a folder, owned by normaluser, hashes equal, a receipt), D-CLIP-07 and D-CLIP-19 pass. UI-CP-04 still failed in the same run (the tab showed "Copied 5 files to the device – paste it there.", not "Copying 5 files …") – to be checked separately. Seen in the same runs: files offered to the device (paste-on-demand) are never fetched by the shell's *Paste* verb in normaluser's session. No TransferRequest, no "clipboard: pasted by …" (nor "… reads every clipboard change") line in the agent log, and nothing in the target folder. A copy sent at once (`offer: false`) lands as CF_HDROP and is fine (D-CLIP-14, level 3). A likely cause is the clipboard across integrity levels: the user's programs may not get the LocalSystem helper's data object, so they fall back to the plain clipboard, where the contents aren't (`VirtualFiles.cs:43-48`). The owner reported the clipboard working in a manual test (privacy-mode-design.md §5.8), so it may be limited to how the lab pastes. D-CLIP now records the formats normaluser's programs see for the offer. D-CLIP-17 passes only because nothing was pasted. UI-CP-04 again in lab run 20261016-212514 (Console admin PC, Server 2025 device, 0.18.15), with the formats recorded: normaluser's programs did see the offer (`FileGroupDescriptorW`, `FileContents`, `Preferred DropEffect`), yet the shell's *Paste* into a folder brought nothing within 120 s, the agent log has no "clipboard: pasted by …" line for it (the image of UI-CP-03 in the same connection was fetched: "pasted by powershell (2116)"), and the tab showed neither "Copying 5 files to the device" nor "Copied 5 files". So the data object does reach the user's session; the shell's *Paste* either doesn't ask it for `FileContents` or the request never reaches the helper. UI-CP-15 in the same run (Console admin PC, lane 2, Server 2025 device, 0.18.15) shows it in an administrator's session too: big99.bin (99 MB) copied on the admin's PC while connected to Admin's console session, the shell's *Paste* (`Shell.Application` … `InvokeVerb('paste')`) into a folder of that session – no "Copying 1 file to the device" within 90 s and no "clipboard: pasted by …" line in the agent log, while the picture of UI-CP-13 had been fetched in the same session a few minutes before ("pasted by powershell (4444)"). Points to the lab's paste rather than the agent: in lab run 20261017-054704 (Console 26H2 admin PC, Server 2025 device, 0.18.15) UI-RDP's file copies through Windows' own RDP clipboard – no RemoteHub code in the path – failed the same way in both directions (UI-CP-10: three files, none arrived; A-CLIP-01: the MSI and the `.rhagent`, none arrived, so msiexec ended with 1619), while text and pictures went through. The lab pastes with `Shell.Application` … `InvokeVerb('paste')` and then waits with `Start-Sleep`; the shell copies delayed files (`FileGroupDescriptorW` / `FileContents`) on a thread of its own that calls back into the clipboard's data object, which lives on the pasting PowerShell's STA thread – and that thread pumps no messages while it sleeps. A ready `CF_HDROP` (D-CLIP-14) needs no such call, which fits. Since 17 Oct every lab paste of files (UI-RDP, D-CLIP, D-MORE2, UI-DESKTOP, UI-MORE) goes through the module's `Invoke-LabPaste` / `Get-LabPasteCode`, which pumps messages until the files are complete and records the clipboard's formats before the paste and the thread's apartment; if the files arrive in the next run, this row can go. Confirmed: with `Invoke-LabPaste` D-CLIP passes 18 of 18 on both platforms in lab run 20261018-162105 – this row can go once UI-CP-04 and UI-CP-15 have passed too. |
| D-CLIP-04 (double offer) | Minor, seen in spike-reg-W11-D-CLIP: one image copied at the device with Windows Forms `SetImage` was offered twice (ids 1 and 2, 22 ms apart). A program that uses the OLE clipboard changes it twice (`OleSetClipboard`, then `OleFlushClipboard`), and `Win32HelperUi.OnClipboard` (`HelperWindows.cs:686`) sends on every change. With paste-on-demand the second offer quietly replaces the first. Without it (an older RemoteHub, or D-CLIP-16), the first copy starts and can end with "A newer copy replaced it." although only one copy was made, and a file copy is read twice. |
| D-BAN-08 (input blocked) | Seen in lab runs 20261016-135543 (W11 and Server), -184035 and -212514 (W11, 0.18.14/0.18.15): with the admin's privacy screen and the device's input blocked, the banner does stay open (1000 px wide instead of the 412 px pill), but the person at the device never reads why their mouse and keyboard don't work. On the lab's 1024 × 768 screen the banner is capped at the screen width less 24 px (`BannerSize`, `HelperWindows.cs:811`), and with "Hide screen", "Hide apps…" and "Disconnect" the heading `Your screen is being viewed and controlled by DESKTOP-GGPVPIO\PSAdmin · mouse and keyboard blocked` (`HelperWindows.cs:350, 354`) is cut with "…" right after the middle dot; the subtitle `Click "Disconnect" to end` (`:355`) is dropped first when space runs out (`PaintBanner`, `:1164-1173`). The console OCR reads `… by DESKTOP-GGPVPIO\PSAdmin · m…`. The part that matters is the one that goes, at a resolution common on older devices and VMs, and with longer account names. Fix: put the state first (e.g. "Mouse and keyboard blocked · controlled by …"), or switch to the short heading plus the state when the full one doesn't fit, and leave out "Hide apps…" while the screen is covered. Still seen in 20261019-102845 (0.18.21, W11 and Server). **Fixed:** where the heading and subtitle don't fit on one line beside the buttons, the open banner grows taller and wraps them instead of cutting (`BannerSize`, `PaintBanner`); unit tests `BannerLayoutTests`. To be confirmed by a lab run. |
| D-DISC-01 / D-FUS-01 (asked while UAC is up) | To confirm – seen once, in lab run 20261016-212514 (W11 lane 2, 0.18.15). A UAC prompt was left open in normaluser's session (D-UAC-01's typed credentials didn't elevate), so that session stayed on the secure desktop. The next two connections to it both got `WaitingForUser` within a second, but neither a `Denied` after the dialog's 30 s nor the service's "No answer in time." (`DesktopEndpoint.cs:871-875`): about 56 s after the start the agent dropped the connection without a Status or a close frame ("An existing connection was forcibly closed by the remote host"). On the normal desktop the same unanswered question ended with `Denied` after 32 s (d-secure-sas-desktop). RemoteHub would show only "connection lost", not that the user was asked and didn't answer. The agent's log of that part was lost to the context's revert; D-SECURE now keeps it (`d-secure-agent-logs-before-signin`). It also closes a leftover prompt after D-UAC-01, so seeing this again needs a connection made on purpose while a UAC prompt is open. |
| S-SHR-02 (slow answer to "Trust this server?") | Seen in lab run 20261016-212514 (Console lane 2, 0.18.15); it passed in 20261016-135543 (W11), where the lab clicked sooner. SFTP asks about the host key from SSH.NET's `HostKeyReceived`, during the key exchange inside `SftpClient.Connect()` (`SftpShare.cs:33-41`), and `Connect()` waits for that exchange only as long as `ConnectionInfo.Timeout` – 20 s (`SftpShare.cs:29`). A user who compares the fingerprint, as the dialog asks, takes longer: `Connect()` then gives up with SSH.NET's timeout error while the dialog is still open, and the tab is not connected. *Trust and connect* clicked after that still stores the key (`accepted(fp)`, `SftpShare.cs:39`; `MainWindow.Shares.cs:190`), so the next open connects without asking – exactly what the run shows: `connected1` false, the key accepted, `connected2` true. With a changed key the late *Cancel* doesn't give "The server's key wasn't accepted – nothing was sent.": the timeout isn't the `SshConnectionException` that `catch … when (refused)` (`SftpShare.cs:46`) maps, so the tab shows the generic error (`refused` false in the same run). Fix: don't ask inside the handshake – refuse an unknown key there, keep its fingerprint, ask after `Connect()` has failed (no time limit) and connect again with the accepted key. The FTPS and WebDAV certificate questions should be checked for the same limit. S-SHARE2 now answers both questions 30 s after they appear, so the case fails every time until this is fixed. |
| UI-AG-04 (RemoteHub ended) | To confirm – seen once, in lab run 20261016-212514 (Console admin PC, Server 2025 device, 0.18.15). With an agent desktop tab of the connection still live (the PIN connection of UI-AD-06, left open by the test), the connection's password was changed to a wrong one and *Tools → PowerShell* opened: the agent refused the sign-in at 16:48:50 ("Refused .\PSAdmin … The user name or password is incorrect.", event 2000), and at 16:48:59 it logged the desktop connection as "connection lost" with its helper ended – RemoteHub's process was gone. The tab never showed the refusal text, the next UI step found only "Program Manager", and UI-AG-04, -07 and -08 failed on the missing window. In run 20261016-135543 (W11 admin PC) the same steps without a live desktop tab gave the expected "… rejected the sign-in: wrong user name or password …". The cause isn't known: the run kept neither RemoteHub's diagnostic log nor the Application log. UI-DESKTOP now writes both down (`<step>-clientGone` in ui-desktop-*.json) and starts RemoteHub again when its process is gone; it also closes its tabs properly now, so this needs a run that opens PowerShell with a wrong password while a desktop tab of the same connection is live. |
| UI-AD-02 / D-INPUT (focus after consent) | Minor, to confirm. The agent's consent dialog takes the foreground (`SetForegroundWindow`, `HelperWindows.cs:881`); when it closes, Windows needn't give the foreground back to the window that had it. In the D-INPUT runs of 16.10.2026 the admin's keys of three of four sessions reached Windows but no window, and in lab run 20261016-212514 the text typed through the tab right after Allow (UI-AD-02) didn't reach the device's Notepad. For the admin it looks as if the keyboard doesn't work until they click into the picture. Fix to consider: remember the foreground window before showing the dialog and give it back (`SetForegroundWindow` from the helper, which holds the foreground then) when the dialog closes. UI-DESKTOP now records whether the tab's own click brought the window to the front (`typedFocus`, `cp01Focus`: "tab", or "device" when the person at the device had to click). Seen again in lab run 20261017-093048 (Console admin PC, Server 2025 device): Notepad had the focus by the tab's own click, yet neither the typed text (UI-AD-02) nor Ctrl+V (UI-CP-01) reached it – so more than the foreground after the dialog may be involved. |
| J-08 (pictures during a tunnel copy) | **Lab, not the product.** Lab run 20261017-093048 (W11 lane 2, 0.18.15) was the first in which the case ran: 2 GB through the jump host arrived unchanged in 472 s, but only 19 pictures came. The case can't measure the picture yet: the desktop job only moves the mouse (the pointer isn't part of the captured picture, so the agent sends a frame only when the screen changes – the stats show 0-3 fps from the start), and its 3000 moves end after about 90 s of a copy that takes several minutes. Fixed in the lab on 17 Oct: a window keeps changing for the whole copy (the module's `Start-LabChangingWindow`, which D-VIDEO now uses too), the desktop stays open until the copy has ended (`waitFile`), and the case also checks that pictures still come in the copy's last 30 s. |
| D-BAN-05 (resume after Disconnect) | **Security issue.** Seen in lab run 20261017-054704 (W11 lane 1, 0.18.15): the user at the device clicked *Disconnect* on the banner, the connection got Ended "The user at the device ended the session.", and a new connection started 27 ms later with the old resume token was continued at once ("Continuing on the device…", Connected) – nobody was asked, although the user had just ended the session. Cause from the code: on a *Disconnect* the endpoint sends the Ended status and leaves the loop (`DesktopEndpoint.cs:245-249`), but the token is revoked only at the very end of `finally` (`:389`), after the close handshake, which waits up to 2 s for RemoteHub's close (`:361-367`). Within that window `DesktopResume.TakeAsync` still finds the entry with no expiry, takes it as "the old connection hasn't noticed yet", cancels the old one and hands the new connection the session with the old `AllowedBy` (`:1099-1106`). RemoteHub itself doesn't resume after Ended, but any client that holds the token (the same account) can skip the user's refusal. The same applies to every end on purpose (RemoteHub's close, idle, refused). Fix: revoke the token (or mark the entry void) before the Ended status is sent – wherever `_endReason` is set without `_lost` – and have `TakeAsync` refuse an entry whose owner is ending on purpose; a unit test that takes the token right after a user end. **Fixed** on branch fixes-20261017 (`261ad28`), passing on W11 and Server 2025 in lab run 20261018-112637 (0.18.17). |
| W-NONADMIN-01 (setup script on Windows clients) | **Security issue.** Seen in lab runs 20261016-212514 (W11 lane 1) and 20261017-054704 (W11 lane 2, 0.18.15); passes on Server 2025. With "Allow local administrators over the network" **not** chosen, RemoteHub's WinRM setup script still leaves `LocalAccountTokenFilterPolicy` = 1 on Windows 11: the test set it to 0 (it wasn't set before), ran the script with local admins off, and read 1 afterwards. Cause: the script always runs `Enable-PSRemoting -Force -SkipNetworkProfileCheck` (`RemotingSetup.cs:190`), and on client editions of Windows that cmdlet sets `LocalAccountTokenFilterPolicy` to 1 itself. So the UAC remote restrictions for local accounts are turned off without the admin asking for it, and the script's own warning ("Local administrators are restricted over the network here", `:244-245`) never shows. Fix: read the value before `Enable-PSRemoting` and, when local administrators weren't chosen, put it back (delete it if it didn't exist) right after - then the warning works as intended. W-SETUP's "set up before" context doesn't see it because it runs `Enable-PSRemoting` itself before setting the value to 0. **Fixed** on branch fixes-20261017 (`1a4a530`), passing on W11 in lab run 20261017-160133 (0.18.16). |
| POL-05 (PowerShell turned off: reason lost) | Seen in lab run 20261017-054704 (W11 lane 2, 0.18.15), cause confirmed with a unit test. The agent refused correctly ("Refused PowerShell from … turned off on this device (policy PowerShellEnabled)", HTTP 404 before any sign-in), but RemoteHub shows the WebSocket's own text "The server returned status code '404' when status code '101' was expected." instead of "PowerShell, files, registry, services and processes are turned off on … by a policy on the device (PowerShellEnabled)". PowerShell wraps a failed open in `PSRemotingDataStructureException` ("An error has occurred which PowerShell cannot handle …"); the transport's `PSRemotingTransportException` with RemoteHub's text is its inner exception, and the `WebSocketException` below that. `PsRemoting.AgentExplain` (`PsRemoting.cs:183-188`) only takes a transport message that contains "RemoteHub Agent" and otherwise falls back to `GetBaseException().Message`; the 404 text from `AgentErrors.Explain` (`AgentTransport.cs:282-283`) is the only one that doesn't name the agent, so it is skipped. The session limit (503) and "not allowed" (403) texts do name it and reach the user. Every tool through the agent is affected (PowerShell tab, files, registry, services, processes, install and update through the agent), also a policy key the agent can't read (S-POL-01: everything is off, 404). Fix: have the agent's transport throw its own exception type (e.g. `AgentRefusedException : PSRemotingTransportException`) and let `AgentExplain` take the first one of that type in the chain, or simply the first `PSRemotingTransportException` from `AgentClientTransport.Connect`; or name the agent in the 404 text. The unit test `PowerShell_turned_off_by_policy_reaches_RemoteHub_as_the_agents_reason` (`AgentTests.cs`) is skipped until then; POL-05 fails until then. POL-04 and POL-06 failed in the same run only because the lab harness reported PowerShell's outer message instead of RemoteHub's text (also J-06 in run 20261016-212514) – the harness now shows what RemoteHub shows (`OpenAgentSession` in `Program.cs`, `LabTools.Open`). Still failing in lab run 20261019-193627 (0.20.3, both platforms). **Fixed** on branch credential-provider-by-default: the transport throws `AgentTransportException` with RemoteHub's text, and `AgentExplain` takes it whatever it says; unit test `PowerShell_turned_off_by_policy_is_explained_and_nothing_is_signed_in`. |

### Existing tests that are not in the sheet

`S-SHARE.Tests.ps1` (network shares: SMB, SFTP, FTP, WebDAV, 12 tests) uses `S-SMB/S-SFTP/S-FTP/S-DAV`. These clash with section 6, where `S` stands for agent sign-in and transport security. Section 10.7 below documents them under their current IDs. Proposal: rename them to **F-** (F-SMB-01..04, F-SFTP-01..03, F-FTP-01..02, F-DAV-01..03) in the `It` names, the `-Tag`s and the sheet. Also add them to `test-lab-cases.md` so the release gate counts them.

---

## Contents

- 4. Lab self-check
- 5. Agent – install, update, uninstall
  - 5.1 First install
  - 5.2 Other install paths
  - 5.3 Update, repair, downgrade
  - 5.4 Uninstall
  - 5.5 Configuration and service behaviour
- 6. Agent – sign-in and transport security (S)
- 7. Agent – PowerShell and remote tools (P, T)
- 8. WinRM setup script (W)
- 9. Agent remote desktop (D)
  - 9.1 Connect, Hello, sessions
  - 9.2 Consent
  - 9.3 Banner and user at the device
  - 9.4 View-only and rights
  - 9.5 Secure desktop, sign-in, lock, user switch
  - 9.6 Ctrl+Alt+Del
  - 9.7 Input
  - 9.8 Picture, encoder, resolution, monitors
  - 9.9 Clipboard
  - 9.10 Idle, reconnect, robustness
  - 9.11 Audit
- 10. RemoteHub on a Windows client (C, R)
  - 10.1 Installer
  - 10.2 Storage, protection, audit
  - 10.3 Policies, updates, telemetry, crash reports
  - 10.4 RDP through RemoteHub (2VM)
  - 10.5 End to end through RemoteHub's window (2VM)
  - 10.6 Recordings
  - 10.8 Notes
- 10.7 Network shares (existing tests, not yet in the sheet)

---

## 4. Lab self-check

Common setup for this section: `Smoke.Tests.ps1` runs once per VM (`-VmName`, `-EvidenceDir`). `BeforeAll` imports `RemoteHub.TestLab` and calls `Reset-LabVm` (revert to the newest checkpoint, start the VM, wait until the heartbeat is OK and PowerShell Direct as `PSAdmin` answers twice in a row, limit 300 s). Accounts come from `lab\lab.json`: `AdminAccount` = PSAdmin, `UserAccount` = normaluser, `ConsoleAccount` = Admin.

#### SMOKE-01 · Console account after revert
P1 · How: PS · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** VM reverted by `Reset-LabVm`. The checkpoint was taken with `Admin` signed in at the console.

**Steps:**
1. The host sends the Ctrl key to the VM through the Hyper-V keyboard (`Send-LabKey ControlKey`) to wake the display.
2. The host calls `Wait-LabDesktop -Account Console` (limit 120 s). Every 3 s it asks the guest over PowerShell Direct which `explorer.exe` processes belong to `Admin` and which sessions run `LogonUI.exe`.
3. In the same loop it takes a Hyper-V thumbnail screenshot and checks it is not blank.
4. The host saves the console screenshot as `smoke-console.png` in the evidence folder.

**Expected:**
- An `explorer.exe` owned by `Admin` runs in a session that has no `LogonUI.exe`, within 120 s.
- The screenshot has more than one colour (not blank).
- `smoke-console.png` exists in the evidence folder.

#### SMOKE-02 · Admin token
P1 · How: PS · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** VM reverted. `PSAdmin` is a member of the local Administrators group.

**Steps:**
1. The host runs `whoami /groups /fo csv` in the guest over PowerShell Direct as `PSAdmin` (`Invoke-LabGuest`).
2. The host picks the row with SID `S-1-5-32-544`.
3. The host checks the row's Attributes.

**Expected:**
- The `S-1-5-32-544` row exists and its Attributes contain "Enabled group" (not "Group used for deny only").

Gap: the sheet also asks for High integrity (`S-1-16-12288` / "Mandatory Label\High Mandatory Level"); the test does not check the integrity level.

#### SMOKE-03 · Standard user
P1 · How: PS · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** VM reverted.

**Steps:**
1. The host runs `Get-LocalGroupMember -SID 'S-1-5-32-544'` in the guest as `PSAdmin` (by SID, so it works on localized Windows).
2. The host strips the computer prefix from each member name.
3. The host compares the list with the accounts from `lab.json`.

**Expected:**
- The member list contains `PSAdmin`.
- The member list does not contain `normaluser`.

#### SMOKE-04 · Address
P1 · How: PS · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** VM reverted and running, network adapter connected.

**Steps:**
1. The host calls `Get-LabVmAddress`: it reads `Get-VMNetworkAdapter -VMName <vm>` every 3 s for up to 120 s.
2. It takes the first IPv4 address that is not link-local (`169.254.*`).

**Expected:**
- An address matching `^\d{1,3}(\.\d{1,3}){3}$` is returned within 120 s.

Gap: the sheet also asks that the host reaches the address (TCP 445 or ICMP); the test does not try to connect.

#### SMOKE-05 · Auto-logon
P1 · How: PS C · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** VM reverted. Lab password from the lab's local config.

**Steps:**
1. `Set-LabAutoLogon -Account User` writes in the guest under `HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon`: `AutoAdminLogon` = 1, `DefaultUserName` = normaluser, `DefaultPassword` = the lab password, `DefaultDomainName` = the computer name; it removes `AutoLogonCount`.
2. It restarts the VM (`Restart-LabVm`) and waits for PowerShell Direct.
3. It calls `Wait-LabDesktop -Account normaluser` (limit 300 s): explorer owned by normaluser in a session without `LogonUI.exe`, screenshot not blank.
4. The test takes a screenshot `smoke-desktop.png`.

**Expected:**
- `Wait-LabDesktop` returns within 300 s.
- `smoke-desktop.png` is not blank (`IsBlank` = false: more than one colour in the sampled pixels).

#### SMOKE-06 · Code in the user's session
P1 · How: S · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** normaluser signed in by auto-logon (SMOKE-05).

**Steps:**
1. `Invoke-LabInSession -Account User` registers a one-off scheduled task `RHLab-<guid>` in the guest: principal normaluser, logon type Interactive, action `powershell.exe -EncodedCommand ...`.
2. The task runs `"$env:USERNAME session=<SessionId of its own process>"` and writes the output to `C:\ProgramData\RemoteHubLab\session\<guid>.txt`.
3. The host waits up to 120 s for the `.done` file, reads the output, then deletes the task and the files.

**Expected:**
- The output matches `^normaluser session=[1-9]` (an interactive session, not session 0).

#### SMOKE-07 · Lock / unlock
P1 · How: S C · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** normaluser signed in by auto-logon and on the desktop.

**Steps:**
1. `Lock-LabSession` runs `rundll32.exe user32.dll, LockWorkStation` in normaluser's session (scheduled task, as in SMOKE-06).
2. The host waits 3 s.
3. The host lists, over PowerShell Direct, the session IDs of `explorer.exe` owned by normaluser and checks whether `LogonUI.exe` runs in one of them.
4. The host saves `smoke-locked.png`.
5. `Unlock-LabSession` sends Ctrl+Alt+Del through the Hyper-V keyboard, waits 2 s, types the lab password (`TypeText`), presses Enter.
6. It calls `Wait-LabDesktop` (limit 60 s).

**Expected:**
- After the lock, `LogonUI.exe` runs in normaluser's session.
- After the unlock, no `LogonUI.exe` in normaluser's session and the screenshot is not blank, within 60 s.

#### SMOKE-08 · Clock
P2 · How: PS · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** VM reverted (the checkpoint may be weeks old; Hyper-V time sync should correct the clock).

**Steps:**
1. Wait for `Reset-LabVm` to finish.
2. Read `[DateTime]::UtcNow` in the guest over PowerShell Direct and on the host right after.
3. Compute the difference.

**Expected:**
- The guest clock is within 2 minutes of the host clock (UTC).
- On failure the report shows both times and the difference.

#### SMOKE-09 · No pending reboot / update
P3 · How: PS · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** VM reverted.

**Steps:**
1. Check in the guest that `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending` and `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` do not exist.
2. Sample `TiWorker.exe` CPU every 15 s for up to 5 minutes.
3. Write the result to the report.

**Expected:**
- No reboot-pending key.
- `TiWorker.exe` is gone or idle (no CPU time added between two samples) within 5 minutes.
- Otherwise the run continues and the report shows a warning (not a failure).

#### SMOKE-10 · Keyboard layout known
P2 · How: S C · Needs: – · Status: ✅ Automated – `Smoke.Tests.ps1`

**Preconditions:** normaluser signed in by auto-logon.

**Steps:**
1. Read the keyboard layouts of the sign-in screen (`HKEY_USERS\.DEFAULT`) and of every account's profile – loaded, or from its `NTUSER.DAT` – from the language list's input methods and `Keyboard Layout\Preload` with its `Substitutes`; save them to `smoke-layout.json`.
2. Start Notepad in normaluser's session and bring it to the front.
3. Type `a-zA-Z0-9 !@#$%` on the console with `Send-LabText` (Hyper-V `TypeText`, shifted symbols as US scan codes).
4. Save the file with Ctrl+S and compare.

**Expected:**
- The text in Notepad equals `a-zA-Z0-9 !@#$%` exactly.
- Every account and the sign-in screen have only the US keyboard 00000409 (00000407 on a de-DE image); a profile with no layout yet (never signed in) or of a deleted account isn't counted.

## 5. Agent – install, update, uninstall

Common setup for this section: every test file starts with `Reset-LabVm`. The agent is installed with `Install-LabAgent`, which runs the C# lab harness `install` command: `AgentEnrollment.Create` (an enrollment file with the lab's enrollment secret, `LabRootharnessab-enrollment.json`; no key – the device makes its own device ID and RSA 3072 certificate, and the pin is that certificate's SHA-256, read back), `AgentInstaller.Prepare`, `AgentInstaller.InstallOverRemoting` (an older agent that refuses enrollment format 2 – the previous release the update cases start from – is installed again with the format-1 file its RemoteHub made, with a PFX) over a PowerShell Direct session as `PSAdmin` (instead of WinRM). Defaults: port 58732, label = the VM's ID from `lab.json`, firewall scope = the host's source address towards the VM (`Get-LabHostAddress`), `VIRTUALDISPLAY=1`. The MSI under test is `LabRoot\msi\RemoteHub-Agent-<ver>-x64.msi`; the previous release is in `LabRoot\msi\previous`. Device state is read with `Get-LabAgentState` (PowerShell Direct as `PSAdmin`).

### 5.1 First install

#### A-INST-01 · Install over remoting (`InstallOverRemoting`)
P1 · How: H · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** VM reverted, no agent installed. MSI under test in `LabRoot\msi`.

**Steps:**
1. The host reads its own address towards the VM (`Get-LabHostAddress`).
2. The harness creates an enrollment file (label = VM ID, port 58732, scope = host address, the lab's enrollment secret).
3. `InstallOverRemoting` creates `%TEMP%\RemoteHub-Agent-<12 hex>` in the guest, uploads `RemoteHub-Agent-x64.msi` and `<label>.rhagent` in chunks and compares SHA-256 checksums.
4. It runs `msiexec /i "...\RemoteHub-Agent-x64.msi" /qn /norestart /l*v "...\install.log" VIRTUALDISPLAY=1 ENROLLMENT="...\<label>.rhagent"` and waits.
5. It reads back `agent.json`, the certificate's SHA-256 and the service status (`fingerprint|port|deviceId|status`), then deletes the temp folder.
6. The test saves the harness answer as `a-inst-install.json`.

**Expected:**
- `Install-LabAgent` returns without error (`InstallOverRemoting` throws "The installer failed (exit code N)" for any exit code other than 0 or 3010).
- `installed.fingerprint` = the pin, the certificate the device made; the claim over the network (`Get-LabAgentClaim`) succeeds and names the same certificate and device ID.
- `installed.port` = 58732.

#### A-INST-02 · Service
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** agent installed as in A-INST-01.

**Steps:**
1. The host reads `Win32_Service` `RemoteHubAgent` and `HKLM:\SYSTEM\CurrentControlSet\Services\RemoteHubAgent` in the guest.

**Expected:**
- State = `Running`.
- StartName = `LocalSystem`.
- StartMode = `Auto` and `DelayedAutostart` = 1 (set by `sc.exe config RemoteHubAgent start= delayed-auto` during enroll).
- PathName matches `RemoteHub Agent\RemoteHubAgent.exe`.

#### A-INST-03 · Health
P1 · How: H · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** agent installed as in A-INST-01.

**Steps:**
1. The harness `health` command calls `AgentInstaller.HealthAsync`: `GET https://<vm>:58732/v1/health`, the TLS certificate accepted only if its SHA-256 = pin.

**Expected:**
- The request succeeds (TLS pinned to the enrolled certificate).
- `product` = "RemoteHub Agent".
- `deviceId` = the enrolled device ID.

Gap: the test does not check `version` = MSI version, `protocol` = 1, or `features` containing `powershell` and `desktop`.

#### A-INST-04 · Certificate
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** agent installed as in A-INST-01.

**Steps:**
1. The host reads `certificateThumbprint` from `agent.json` and loads `Cert:\LocalMachine\My\<thumbprint>` in the guest.
2. It computes the SHA-256 of the raw certificate and reads the private key's export policy (CNG `ExportPolicy` or CSP `Exportable`).

**Expected:**
- The certificate exists in LocalMachine\My.
- SHA-256 = pin.
- FriendlyName = "RemoteHub Agent".
- Subject = `CN=<deviceId>, O=RemoteHub Agent`.
- HasPrivateKey = true, key not exportable (`Exportable` = false; an unknown key type gives `$null` and fails).

Gap: the test does not check RSA 3072, validity of 2 years (`NotAfter` is read but not compared) or EKU server authentication (1.3.6.1.5.5.7.3.1).

#### A-INST-05 · agent.json
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** agent installed as in A-INST-01.

**Steps:**
1. The host reads `C:\ProgramData\RemoteHub\Agent\agent.json` in the guest and parses it.

**Expected:**
- `deviceId` = the enrolled device ID.
- `listenPort` = 58732.
- `allowedGroups` = exactly `["S-1-5-32-544"]`.

Gap: the test does not check `deviceLabel` (= VM ID), `certificateThumbprint` (only used to find the certificate), `virtualDisplay` (`auto`), `logRetentionDays` 30 or `maxSessions` 20.

#### A-INST-06 · Firewall
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** agent installed as in A-INST-01 with scope = host address.

**Steps:**
1. The host reads `Get-NetFirewallRule -DisplayName 'RemoteHub Agent'` with its port, address and application filters in the guest.

**Expected:**
- Exactly 1 rule.
- Enabled = True, Direction = Inbound, Action = Allow, Protocol = TCP, LocalPort = 58732.
- Program ends with `RemoteHub Agent\RemoteHubAgent.exe`.
- RemoteAddress = exactly the host address.

Gap: "all profiles" is not checked (the rule's `Profile` is read but not compared with `Any`).

#### A-INST-07 · Folder ACL
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** agent installed as in A-INST-01.

**Steps:**
1. The host runs `Get-Acl C:\ProgramData\RemoteHub\Agent` in the guest and reads owner, protection and access rules as SIDs.

**Expected:**
- Owner = `S-1-5-32-544`.
- Inheritance protected = true.
- The set of SIDs in the rules is exactly `S-1-5-18`, `S-1-5-32-544`.
- No rule for Users (`S-1-5-32-545`), Authenticated Users (`S-1-5-11`) or Everyone (`S-1-1-0`).

#### A-INST-08 · No secrets left
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** agent installed as in A-INST-01.

**Steps:**
1. The host lists in the guest: folders `RemoteHub-Agent-*` in `C:\Users\*\AppData\Local\Temp` and `C:\Windows\Temp`.
2. It searches `*.rhagent` recursively in `C:\ProgramData` and `C:\Users\*\AppData\Local\Temp`.

**Expected:**
- Both lists are empty (`Leftovers` empty).

Gap: the test does not search for PFX files, does not look for `.rhagent` in `C:\Windows\Temp`, and does not check that the cached MSI in `C:\Windows\Installer` contains no PFX.

#### A-INST-09 · Event source
P2 · How: PS E · Needs: – · Status: ✅ Automated – `A-INST.Tests.ps1`

**Preconditions:** agent installed as in A-INST-01.

**Steps:**
1. The host checks `HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\RemoteHub Agent` in the guest.
2. It reads all `C:\ProgramData\RemoteHub\Agent\logs\agent-*.log` lines.
3. `AfterAll` copies the log folder to `a-inst-agent-logs` in the evidence folder.

**Expected:**
- The event source key exists.
- The log contains "Enrolled as device <deviceId>".

Gap: the sheet asks for the start line; the test checks the enroll line. Not checked: "RemoteHub Agent <version> starting on port 58732 (device <deviceId>, certificate <SHA-256>)".

#### A-INST-10 · Recovery actions
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** agent installed (A-INST-01).

**Steps:**
1. Run `sc.exe qfailure RemoteHubAgent` in the guest.
2. Note the PID of `RemoteHubAgent.exe` and stop it with `Stop-Process -Force`.
3. Poll `Get-Service RemoteHubAgent` and the process every 2 s for 30 s.
4. Call `/v1/health` with the pin.

**Expected:**
- `qfailure` shows RESTART three times with 10000 ms delay and RESET_PERIOD 86400 s (from `util:ServiceConfig` in `Package.wxs`).
- The service is `Running` again with a new PID within 30 s.
- Health answers with the same device ID.

#### A-INST-11 · Service survives a reboot
P1 · How: PS H · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** agent installed (A-INST-01), pin known.

**Steps:**
1. `Restart-LabVm` and wait for PowerShell Direct.
2. Call `Get-LabAgentHealth -Pin <pin>` every 10 s for up to 3 minutes.
3. Read `Get-LabAgentState`.

**Expected:**
- Health answers within 3 minutes of the restart (delayed start), TLS pinned to the same pin.
- Service `Running`, the certificate SHA-256 is unchanged.

#### A-INST-12 · Nothing extracted
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** agent installed and the service started at least once.

**Steps:**
1. List `C:\Windows\Temp\.net` and `C:\Windows\System32\config\systemprofile\AppData\Local\Temp\.net` (and the SYSTEM profile's other temp folders) in the guest.
2. List the DLLs in `C:\Program Files\RemoteHub Agent`.
3. List the modules loaded by `RemoteHubAgent.exe` (`Get-Process -Module`) that are not from `C:\Windows`.

**Expected:**
- No `.net` folder in those places.
- Every non-Windows module of the agent process is loaded from `C:\Program Files\RemoteHub Agent`.

#### A-INST-13 · Firewall scope Any
P2 · How: H PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent.

**Steps:**
1. `Install-LabAgent -Scope ''` (the harness gets no `--scope`, the enrollment has `firewallRemoteAddresses` = "").
2. Read the rule with `Get-LabAgentState`.
3. Call health from the host.

**Expected:**
- One rule "RemoteHub Agent", RemoteAddress = `Any` (enroll runs `netsh ... remoteip=any`).
- The agent log says "firewall scope any".
- Health answers.

#### A-INST-14 · Firewall scope list
P2 · How: H PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent.

**Steps:**
1. `Install-LabAgent -Scope '10.0.0.0/8, 192.168.5.10-192.168.5.20, <host>'`.
2. Read the rule's address filter.
3. Call health from the host.

**Expected:**
- RemoteAddress lists exactly `10.0.0.0/255.0.0.0`, `192.168.5.10-192.168.5.20` and the host address (Windows may show the CIDR as a mask).
- Health answers from the host.

#### A-INST-15 · Other port
P2 · How: H PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent.

**Steps:**
1. `Install-LabAgent -Port 58800`.
2. Read `Get-NetTCPConnection -State Listen -OwningProcess <agent PID>` and the rule in the guest.
3. Call health on 58800 and on 58732.

**Expected:**
- The harness answer has `installed.port` = 58800; `agent.json` `listenPort` = 58800.
- The agent listens on 58800; the rule's LocalPort = 58800.
- Health on 58800 answers; a connection to 58732 fails.

#### A-INST-16 · Allowed groups by name
P1 · How: H PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent. For the second part a de-DE VM.

**Steps:**
1. Turn the dialog text "Administrators" into the group list with `AgentInstaller.ParseGroups`.
2. Create the enrollment with that list and install (the harness needs a `--groups` option for this).
3. Read `allowedGroups` from `agent.json`.
4. Repeat on the de-DE VM and sign in through the agent as `PSAdmin`.

**Expected:**
- `allowedGroups` = `["S-1-5-32-544"]` on both VMs.
- `PSAdmin` can open a PowerShell session through the agent on de-DE.

Note: `ParseGroups` maps only the English word "Administrators" (any case) to `S-1-5-32-544`. A typed "Administratoren" is stored as typed (a name), not as the SID. The harness `install` command has no option for groups yet.

#### A-INST-17 · Custom allowed group
P2 · How: H PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted. Local group "RH Operators" created with member normaluser; PSAdmin not a member.

**Steps:**
1. Install with `allowedGroups` = `["RH Operators"]` (harness `--groups` option needed).
2. `Invoke-LabAgentCommand -Account User -Script 'whoami'`.
3. `Invoke-LabAgentCommand -Account Admin -Script 'whoami'`.

**Expected:**
- normaluser gets a session; the output is `<computer>\normaluser`.
- PSAdmin is refused with HTTP 403 ("not a member of an allowed group").

#### A-INST-18 · Label with spaces / umlauts
P3 · How: H · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent.

**Steps:**
1. `Install-LabAgent -Label 'Büro PC 7'`.
2. Read `agent.json` (UTF-8).
3. Open a desktop connection through the harness and read the Hello message.

**Expected:**
- The enrollment file is named `Büro-PC-7.rhagent` (spaces become `-`, `AgentEnrollment.FileName`) and the install succeeds.
- `deviceLabel` = "Büro PC 7" in `agent.json` and in the desktop Hello.

#### A-INST-19 · Virtual display off at install
P2 · How: H PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent.

**Steps:**
1. `Install-LabAgent -VirtualDisplay $false` (msiexec gets `VIRTUALDISPLAY=0`).
2. Read `Get-LabAgentState` (`VirtualDisplay` = PnP devices with hardware ID `Root\MttVDD`).
3. Run `pnputil /enum-drivers` and list `Cert:\LocalMachine\TrustedPublisher`.

**Expected:**
- `agent.json` `virtualDisplay` = `off`.
- No `Root\MttVDD` device, no `MttVDD.inf` driver package.
- No certificate with thumbprint `3CF8CF26D8BA266C3A483AB7D26D4A818E317D76` in TrustedPublisher; no `virtual-display.txt`.

#### A-INST-20 · Virtual display on at install
P2 · How: H PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent, SignPath certificate not in TrustedPublisher.

**Steps:**
1. `Install-LabAgent` (default `VIRTUALDISPLAY=1`).
2. Read the `Root\MttVDD` device, `pnputil /enum-drivers`, TrustedPublisher, `C:\ProgramData\RemoteHub\Agent\virtual-display.txt` and the agent log.
3. Hash the files in `C:\Program Files\RemoteHub Agent\VirtualDisplay`.

**Expected:**
- One `Root\MttVDD` device, disabled (ConfigManagerErrorCode 22).
- File hashes equal `VirtualDisplay.Files` (`MttVDD.inf` 550D211F…, `MttVDD.dll` C9CA837F…, `mttvdd.cat` 08A0093F…, `SignPathFoundation.cer` 69DFD3E1…).
- Thumbprint `3CF8CF26D8BA266C3A483AB7D26D4A818E317D76` in LocalMachine\TrustedPublisher.
- `virtual-display.txt` contains `trustAdded=1`; `C:\VirtualDisplayDriver\vdd_settings.xml` exists.
- Log: "Virtual display installed (device …, driver package oem….inf, publisher trusted) – switched on only while a connection needs it".

#### A-INST-21 · Trust already present
P3 · How: PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted. The SignPath certificate (thumbprint `3CF8…7D76`) imported into LocalMachine\TrustedPublisher before the install.

**Steps:**
1. `Install-LabAgent`.
2. Read `virtual-display.txt`.
3. `Uninstall-LabAgent`.
4. List TrustedPublisher.

**Expected:**
- `virtual-display.txt` contains `trustAdded=0`.
- After the uninstall the certificate is still in TrustedPublisher.

#### A-INST-22 · SoftwareSASGeneration set
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted; `SoftwareSASGeneration` not set under `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`.

**Steps:**
1. Read the value before the install (`Get-LabAgentState` `SoftwareSas`).
2. `Install-LabAgent`.
3. Read the value, the marker file and the agent log.

**Expected:**
- Before: not set. After: `SoftwareSASGeneration` = 1 (DWORD).
- `C:\ProgramData\RemoteHub\Agent\sas.agent` exists.
- Log: "Ctrl+Alt+Del: allowed services to send it (SoftwareSASGeneration = 1)".

#### A-INST-23 · SAS policy respected
P2 · How: PS H · Needs: – · Status: ✅ Automated – `A-MORE.Tests.ps1`

**Preconditions:** VM reverted; `SoftwareSASGeneration` = 0 set before the install (simulated policy), no `sas.agent`.

**Steps:**
1. `Install-LabAgent`.
2. Read the value and the marker.
3. Open a desktop session through the harness (`cad` command) and press Ctrl+Alt+Del.

**Expected:**
- `SoftwareSASGeneration` stays 0; no `sas.agent` file.
- The agent answers that Ctrl+Alt+Del is blocked: "Ctrl+Alt+Del from software is turned off by a policy on this device." (see D-SAS-04).

#### A-INST-24 · selftest on the device
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** agent installed (A-INST-01).

**Steps:**
1. Count the agent log lines and the certificates in LocalMachine\My.
2. Run `& 'C:\Program Files\RemoteHub Agent\RemoteHubAgent.exe' selftest` in the guest and capture output and exit code.
3. Count again.

**Expected:**
- The output ends with "Self-test passed."; exit code 0.
- No new log line, no new certificate, `agent.json` unchanged (the self-test uses 127.0.0.1 and a throw-away certificate).

#### A-INST-25 · Port in use
P2 · How: PS H · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted. Another process listens on TCP 58732 in the guest (for example a `System.Net.Sockets.TcpListener` in a background job).

**Steps:**
1. `Install-LabAgent` (expect an error).
2. Read `Get-LabAgentState` and the agent log.

**Expected:**
- Either the install fails (the MSI's `ServiceControl` start fails, msiexec returns an error and rolls back) or the service is not running. No silent switch to another port.
- The agent log contains "RemoteHub Agent could not start: …".

Note: the service is `Vital="yes"` with `Start="install"`, so a service that doesn't start fails the whole install (rollback). Expect the "install fails" branch.

#### A-INST-26 · Install log for failures
P2 · How: H · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent.

**Steps:**
1. Create a damaged `.rhagent` (for example `{ "format": 1 }`).
2. Run the harness install with that file instead of a new enrollment (needs an `--enrollment-file` option for `install`).
3. Read the exception message.

**Expected:**
- The install throws "The installer failed (exit code 1603)." followed by lines from `install.log` that match `WixQuietExec|Error \d{4}|failed|could not|No \.rhagent` (`LogTailScript`, last 12 lines), including the enroll error ("The enrollment has no valid device ID.").

### 5.2 Other install paths

#### A-HAND-01 · MSI by hand next to the `.rhagent`
P1 · How: H S · Needs: – · Status: ✅ Automated – `A-HAND.Tests.ps1`

**Preconditions:** VM reverted. Enrollment file created on the host with the lab's enrollment secret (`AgentEnrollment.Create`, no key). `Admin` signed in at the console.

**Steps:**
1. Copy `RemoteHub-Agent-x64.msi` and `<label>.rhagent` into one folder in the guest (for example `C:\Users\Admin\Desktop\agent`).
2. In Admin's session (scheduled task, highest privileges) run `msiexec /i RemoteHub-Agent-x64.msi /qn` from that folder without `ENROLLMENT`.
3. On the host, claim the agent every 10 s, like `WaitForClaim` does (`AgentInstaller.ClaimAsync`, up to 30 min).

**Expected:**
- msiexec exit 0; the `.rhagent` file is gone from the folder.
- The claim succeeds within 10 s of the service start (one poll interval), with the device ID and the certificate fingerprint in the device's `agent.json` and `LocalMachineMy` – the device made them itself.

#### A-HAND-02 · Two `.rhagent` files in the folder
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-HAND.Tests.ps1`

**Preconditions:** VM reverted. Two valid `.rhagent` files and the MSI in one folder.

**Steps:**
1. Run `msiexec /i RemoteHub-Agent-x64.msi /qn /l*v <log>` from that folder as PSAdmin.
2. Read the log and `Get-LabAgentState`.

**Expected:**
- msiexec exit 1603.
- The log contains "More than one .rhagent file in <folder> – keep only the one for this device."
- No product, service, rule, certificate or `agent.json`; both `.rhagent` files still there.

#### A-HAND-03 · No `.rhagent` and no config
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-HAND.Tests.ps1`

**Preconditions:** VM reverted. MSI alone in a folder; no `agent.json`.

**Steps:**
1. Run `msiexec /i RemoteHub-Agent-x64.msi /qn /l*v <log>` as PSAdmin.
2. Read the log and `Get-LabAgentState`.

**Expected:**
- msiexec exit 1603.
- The log contains "No .rhagent enrollment file found next to the installer."
- No product, service, rule, `O=RemoteHub Agent` certificate or `agent.json` (rollback runs `RemoteHubAgent.exe cleanup`).

#### A-HAND-04 · `ENROLLMENT=<path>` property
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-HAND.Tests.ps1`

**Preconditions:** VM reverted. MSI in one folder, `.rhagent` in another (for example `C:\Enroll\dev.rhagent`).

**Steps:**
1. Run `msiexec /i <msi> /qn ENROLLMENT="C:\Enroll\dev.rhagent"`.
2. Read `agent.json` and check the file.

**Expected:**
- msiexec exit 0; the claim succeeds, and `deviceId` and the certificate are the ones it names.
- `C:\Enroll\dev.rhagent` is deleted.

#### A-HAND-05 · Enrollment format 3 / format-1 file / bad key ID / bad secret / port 0 / empty groups
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-HAND.Tests.ps1`

**Preconditions:** VM reverted. Six `.rhagent` files made from a valid one, each with one field changed (`format` 3, `format` 1 – as a file from an older RemoteHub, which held the device's PFX –, `keyId`, `secret`, `listenPort`, `allowedGroups`).

**Steps:**
1. For each file: install with `ENROLLMENT=<file>` and a verbose log.
2. Read the log and `Get-LabAgentState`, then revert.

**Expected:**
- Each install fails (1603) and rolls back (no service, rule, certificate, `agent.json`).
- The log shows: "Enrollment format 3 is not supported by this agent version." / "This enrollment file was made by an older RemoteHub and holds a device key, which this agent no longer accepts. …" / "The enrollment has no valid key ID." / "The enrollment has no valid secret." / "Port 0 is not valid." / "No allowed groups – nobody could connect."

#### A-HAND-06 · Interactive install (UI, not /qn)
P3 · How: S · Needs: – · Status: ✅ Automated – `A-MORE.Tests.ps1`

**Preconditions:** VM reverted. MSI and `.rhagent` in one folder; Admin signed in.

**Steps:**
1. Start `msiexec /i RemoteHub-Agent-x64.msi /qb` in Admin's session (elevated); the same session notes the title of msiexec's visible window until msiexec ends (a first install takes only a few seconds – a look from outside afterwards can miss the window).
2. Nothing to click: Windows Installer's progress window runs to the end on its own.
3. Read `Get-LabAgentState`.

**Expected:**
- A window titled "RemoteHub Agent" was shown; msiexec exit 0 (or 3010).
- Installed, enrolled with the `.rhagent` next to the MSI, service running.
- `virtualDisplay` = `off`, no `Root\MttVDD` device.

Note: the MSI has no custom dialog and no virtual-display checkbox (only the `VIRTUALDISPLAY` property); the UI is the standard Windows Installer progress UI. The checkbox exists only in RemoteHub's install dialog.

#### A-CLIP-01 · Clipboard install from RemoteHub
P2 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** two VMs: RemoteHub on the admin VM, device VM reverted. RDP connection with clipboard redirection.

**Steps:**
1. In RemoteHub, right-click the connection → RemoteHub Agent → Install RemoteHub Agent, method Clipboard (UI Automation).
2. Open the RDP session to the device; paste both files into one folder.
3. Run `RemoteHub-Agent-x64.msi` as administrator in that session.
4. Watch the connection in RemoteHub.

**Expected:**
- RemoteHub shows "Copied RemoteHub-Agent-x64.msi and <label>.rhagent to the clipboard."
- After the install the connection is claimed and marked installed on the agent's first answer (`WaitForClaim`, audit "Claimed – installed by hand, certificate SHA-256 …").

#### A-CLIP-02 · Staging cleanup on the admin's PC
P1 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** A-CLIP-01 done up to the clipboard copy.

**Steps:**
1. Check `%TEMP%\RemoteHub-Agent\<12 hex>` on the admin VM.
2. Close RemoteHub normally; check again.
3. Repeat the copy, kill RemoteHub (`Stop-Process -Force`), start it again; check again.

**Expected:**
- The folder exists while RemoteHub runs (files must stay until pasted).
- It is gone after a normal exit and after the next start following a crash (`CleanTemp(TimeSpan.Zero)`).

#### A-CLIP-03 · Ask before sending the key through a desktop tab
P1 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RemoteHub with an agent desktop tab open to device B that sends the clipboard.

**Steps:**
1. Start a clipboard install for device A.
2. Read the dialog through UI Automation; press Cancel.

**Expected:**
- Dialog "Copy the enrollment secret?" naming device B and `<A label>.rhagent` ("…which holds RemoteHub's enrollment secret. Close that tab first, or copy anyway?").
- After Cancel: "Cancelled – close the remote desktop tab, then try again."; the clipboard holds no `.rhagent`.

### 5.3 Update, repair, downgrade

Common setup for A-UPD, A-SAME and A-DOWN: `A-UPD.Tests.ps1`. Each context reverts the VM and installs an agent. "Update through the agent" is the harness `update` command, which mirrors the ThroughAgent branch of `MainWindow.RunAgentInstall`: health with the old pin, PowerShell session through the agent as PSAdmin, `AgentInstaller.StartUpdateThroughAgent` (upload to `%TEMP%\RemoteHub-Agent-<12 hex>`, then `cmd /c start "" /wait msiexec ... /l*v %TEMP%\RemoteHub-Agent-update.log ... & rd /s /q <folder>` started through WMI), `PackageVersion(msi)`, `WaitForRestartAsync` (wait up to 90 s for the agent to go away, then up to 3 min for an answer with the expected version), and the same verdict: `updated`, `notInstalled`, `noAnswer` or `failed`. It also reports what the old pin answers afterwards (`oldPinNow`).

#### A-UPD-01 · Update through the agent (`StartUpdateThroughAgent`)
P1 · How: H · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** VM reverted; previous release installed with `Install-LabAgent -Msi <previous>`; policy `ConsentTimeoutSeconds` = 45 set (for A-UPD-06).

**Steps:**
1. Read the device state (`$before`).
2. `Update-LabAgent -Pin <pin> -Msi <under test> -HoldSession` (a second agent session stays open, for A-UPD-08).
3. Wait up to 3 min until no staging folder is left (for A-UPD-02).
4. Read the device state (`$after`), the policy and the update log; save as `a-upd-through-agent.json`.

**Expected:**
- `before` (old health version) = previous version; `expectedVersion` = version of the MSI under test.
- verdict = `updated` ("Updated – RemoteHub Agent <new> is running.").
- Health after the update: `version` = new version, `deviceId` = the enrolled ID.
- The old pin answers with the new version (`oldPinNow` = new version): same certificate.
- Installed product version starts with the new version; service `Running`.

Gap: "down within 90 s" is not asserted. `WaitForRestartAsync` moves on after 90 s even if the agent never went down.

#### A-UPD-02 · Temp folder removed
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** update from A-UPD-01 started.

**Steps:**
1. Every 5 s for up to 3 min, list `RemoteHub-Agent-*` folders in `C:\Users\*\AppData\Local\Temp` and `C:\Windows\Temp`, and `*.rhagent` files.
2. Read `RemoteHub-Agent-update.log` from PSAdmin's `%TEMP%` (the account that signed in through the agent). Keep the lines that match `Installation (success or error status|operation completed)|completed successfully|Return value 3`.

**Expected:**
- No staging folder and no `.rhagent` left within 3 min.
- The update log still exists and matches `completed successfully` or `success or error status: 0`.

#### A-UPD-03 · Identity kept
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** A-UPD-01 done; `$before` and `$after` read.

**Steps:**
1. Compare `agent.json`, the certificate, the firewall rule and the service between `$before` and `$after`.

**Expected:**
- `deviceId`, `certificateThumbprint`, `listenPort`, `allowedGroups` unchanged.
- Certificate SHA-256 = the original pin, key not exportable.
- Exactly 1 `O=RemoteHub Agent` certificate in LocalMachine\My.
- Exactly 1 firewall rule, same RemoteAddress as before.
- Service still delayed-auto (`DelayedAutostart` = 1).

#### A-UPD-04 · New device certificate on update
P1 · How: H PS · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** VM reverted; previous release installed.

**Steps:**
1. `Update-LabAgent -Msi <under test> -NewIdentity` ("Enroll again"): the harness sends a new enrollment file (label = VM ID, scope = host address) with the update; it waits for the old certificate to stop answering, then claims the new one – its pin is `expectedPin`.
2. Read the device state; save `a-upd-new-identity.json`.

**Expected:**
- verdict = `updated`; `expectedPin` ≠ old pin.
- Health with the new pin returns the new device ID.
- The old pin is refused (`oldPinNow` starts with "refused").
- Certificate SHA-256 = new pin; `agent.json` `deviceId` = new ID.
- Exactly 1 agent certificate, exactly 1 firewall rule, no leftovers.

#### A-UPD-05 · Update over remoting
P2 · How: H · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** VM reverted; previous release installed.

**Steps:**
1. Read `$before`.
2. `Install-LabAgent -Msi <under test> -KeepIdentity -VirtualDisplay $false`: `InstallOverRemoting` without an enrollment file and with `VIRTUALDISPLAY=0` (enroll logs "Update without enrollment file: keeping the existing device identity").
3. Read `$after` and health with the original pin; save `a-upd-over-remoting.json`.

**Expected:**
- `installed.fingerprint` = original pin; `installed.deviceId` = original ID.
- Health `version` = new version.
- `certificateThumbprint` unchanged; exactly 1 agent certificate.

#### A-UPD-06 · Update keeps policies and logs
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** A-UPD-01 done; `ConsentTimeoutSeconds` = 45 was set under `HKLM\SOFTWARE\Policies\RemoteHub\Agent` before the update.

**Steps:**
1. Read the policy key after the update.
2. Compare the agent log lines before and after.

**Expected:**
- `ConsentTimeoutSeconds` = 45.
- The first log line from before the update is still in the log.
- The log contains "RemoteHub Agent <new version> starting".

#### A-UPD-07 · Update with virtual display change
P3 · How: H PS · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** same context as A-UPD-05 (previous release installed with `VIRTUALDISPLAY=1`, update over remoting with `VIRTUALDISPLAY=0`).

**Steps:**
1. Check `$before.VirtualDisplay` (PnP devices with hardware ID `Root\MttVDD`).
2. Check `$after.VirtualDisplay`.

**Expected:**
- If the previous release installed the device: no `Root\MttVDD` device after the update.
- If it did not, the test is skipped ("agent <old> didn't install the virtual display driver").

Gap: "empty keeps it" is not tested. Note: RemoteHub always passes `VIRTUALDISPLAY=1` or `0` (`InstallScript` / `DetachedInstallScript`), never empty. "Empty" only happens with a hand-run msiexec.

#### A-UPD-08 · Update during an open PowerShell session
P2 · How: H · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** A-UPD-01 run with `-HoldSession`.

**Steps:**
1. Before the update, the harness opens a second PowerShell session through the agent and reads its `$PID`.
2. After the update it runs `$PID` in that held session again.
3. The test opens a new session through the updated agent (`Invoke-LabAgentCommand -Script '$PID'`).

**Expected:**
- `heldPid` > 0; `heldAfter` starts with "ended" (the held session no longer works).
- The new session returns a PID > 0 that differs from `heldPid`.

#### A-UPD-09 · Update during an open desktop session
P2 · How: H S · Needs: – · Status: ✅ Automated – `A-MORE.Tests.ps1`

**Preconditions:** previous release installed; normaluser signed in at the console.

**Steps:**
1. Open a desktop session through the harness (consent None with policy, or Ask with the dialog accepted through UIA).
2. Update through the agent.
3. Check the guest for the desktop helper process (`RemoteHubAgent.exe` in the user's session) and the banner window.
4. Open a new desktop session.

**Expected:**
- The first desktop session ends; no helper process and no banner left in the user's session.
- The new desktop session connects and frames arrive.

#### A-UPDFAIL-01 · Broken MSI through the agent
P1 · How: H · Needs: – · Status: ✅ Automated – `A-UPDFAIL.Tests.ps1`

**Preconditions:** VM reverted; previous release installed. The host makes a copy of the MSI under test with a `LaunchCondition` row `LabNeverTrue = 1` / "Lab: this package is broken on purpose." (`New-LabBrokenMsi -Kind LaunchCondition`).

**Steps:**
1. `Update-LabAgent -Msi <broken copy>`.
2. Poll health with the old pin every 5 s, up to 36 tries (3 min).
3. Read the device state, the update log lines matching `Return value 3|success or error status|WixQuietExec|Error \d{4}|Lab: `, and copy the agent logs; save `a-updfail-launch-condition.json`.

**Expected:**
- verdict = `notInstalled`; message contains "still runs RemoteHub Agent <previous version>" ("The update didn't install: the device still runs RemoteHub Agent <old> (expected <new>).").
- The update log contains "Lab: this package is broken on purpose".
- Old agent kept: health answers with the old pin, `version` = previous version, same device ID; product version = previous; service `Running`; certificate SHA-256 = old pin; 1 agent certificate; 1 firewall rule; `agent.json` `deviceId` unchanged.

Note: the broken package has the version of the build under test (newer than the installed one), not the installed version. The verdict takes about 4.5 min (90 s down window plus 3 min up window).

#### A-UPDFAIL-02 · Upgrade that fails in enroll
P1 · How: PS H · Needs: – · Status: ✅ Automated – `A-UPDFAIL.Tests.ps1`

**Preconditions:** VM reverted; previous release installed. Damaged enrollment file `damaged.rhagent` with the content `{ "format": 1, "this is": "not an enrollment" }`.

**Steps:**
1. `Update-LabAgent -Msi <under test> -EnrollmentFile damaged.rhagent`: the file goes into the staging folder and msiexec gets `ENROLLMENT=<it>`.
2. `RemoteHubAgent.exe enroll` fails during the upgrade, after the old version was removed. The upgrade rolls back. The old product's `cleanup` does not run, because its condition excludes `UPGRADINGPRODUCTCODE`, and the rollback `cleanup` runs only on a first install.
3. Poll health with the old pin (up to 3 min); read the state and the update log; save `a-updfail-enroll-fails.json`.

**Expected:**
- The update log contains "Return value 3".
- Old agent kept (same checks as A-UPDFAIL-01): verdict not `updated`, health = previous version with the old pin, same device ID, 1 certificate, 1 rule, same `deviceId`.
- No staging folder and no `.rhagent` left.

Gap: the test does not check the enroll error text ("enroll failed: The enrollment has no valid device ID.") in the agent log or the MSI log.

#### A-UPDFAIL-03 · Update that never comes back
P2 · How: H · Needs: – · Status: ✅ Automated – `A-MORE.Tests.ps1`

**Preconditions:** previous release installed.

**Steps:**
1. Prepare the guest so the new service can't start after the upgrade (for example a scheduled task that starts a TCP listener on 58732 as soon as the old agent stops).
2. Update through the agent and time it.

**Expected:**
- verdict = `noAnswer`, message "The agent didn't answer again within a few minutes."
- Time to the verdict is about 90 s + 3 min at most.

Note: `WaitForRestartAsync` waits up to 90 s for the agent to go down and then up to 3 min for it to come back. The time limit is 3 min after the agent went away, not 3 min in total. Because the service is Vital, a service that doesn't start rolls the upgrade back, so the old agent may come back. The verdict may then be `notInstalled`, not `noAnswer`.

#### A-UPDFAIL-04 · File that isn't a package
P1 · How: H · Needs: – · Status: ✅ Automated – `A-UPDFAIL.Tests.ps1`

**Preconditions:** VM reverted; previous release installed. The host makes `truncated\RemoteHub-Agent-x64.msi`: the first 64 KB of the MSI under test (`New-LabBrokenMsi -Kind Truncated`).

**Steps:**
1. `Update-LabAgent -Msi <truncated file>`.
2. Poll health with the old pin (up to 3 min); read the state; save `a-updfail-not-a-package.json`.

**Expected:**
- verdict is not `updated`.
- Old agent kept: health = previous version with the old pin, same device ID, product version = previous, service `Running`, same certificate, 1 certificate, 1 rule, same `deviceId`.

Note: in the current code `AgentInstaller.PackageVersion` returns null for an unreadable file. With no expected version, `WaitForRestartAsync` returns the first answer after the 90 s window. Both `MainWindow.RunAgentInstall` and the harness then report "Updated – RemoteHub Agent <old> is running.", so this test will probably fail until RemoteHub treats a package without a readable version as failed. The sheet's "reported as failed" is not checked as such; the test only asserts "not updated".

#### A-SAME-01 · Reinstall the same version
P3 · How: H · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** VM reverted; MSI under test installed. `MajorUpgrade AllowSameVersionUpgrades="yes"`.

**Steps:**
1. `Update-LabAgent -Msi <under test>` (same version, no new identity).
2. Save `a-upd-same-version.json`.

**Expected:**
- verdict = `updated`.
- Health `deviceId` = the enrolled ID.
- The old pin still answers with the same version (`oldPinNow` = version under test).

Gap: the expected version equals the running version, so `updated` doesn't prove a reinstall happened. The test doesn't check a restart (for example a new agent PID or a second "starting" log line).

#### A-SAME-02 · Same version with a new device certificate
P2 · How: H · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** same context as A-SAME-01, after its update.

**Steps:**
1. `Update-LabAgent -Msi <under test> -NewIdentity` (new enrollment, claimed after the restart, same version).
2. Read the device state.

**Expected:**
- verdict = `updated` (the new pin answers).
- The old pin is refused (`oldPinNow` starts with "refused").
- Certificate SHA-256 = new pin; exactly 1 agent certificate.

#### A-DOWN-01 · Downgrade
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-UPD.Tests.ps1`

**Preconditions:** VM reverted; MSI under test installed.

**Steps:**
1. Copy the previous release MSI to `C:\Windows\Temp\RemoteHubLab` in the guest.
2. As PSAdmin run `msiexec /i "C:\Windows\Temp\RemoteHubLab\<old msi>" /qn /norestart /l*v C:\Windows\Temp\RemoteHubLab\downgrade.log` and wait.
3. Keep the log lines matching `newer version|Return value 3|success or error status`.
4. Read the device state and health with the pin; save `a-upd-downgrade.json`.

**Expected:**
- msiexec exit code ≠ 0.
- The log contains "A newer version of the RemoteHub Agent is already installed."
- Product version still the version under test; health `version` = version under test; `deviceId` unchanged.

#### A-REPAIR-01 · Repair from the MSI
P3 · How: PS · Needs: – · Status: ✅ Automated – `A-MORE.Tests.ps1`

**Preconditions:** agent installed (A-INST-01); state and pin read.

**Steps:**
1. Read `NoRepair` in the product's Uninstall key.
2. Copy the agent MSI to the device under the name it was registered with (RemoteHub deletes the folder it installed from, and the cached package in `C:\Windows\Installer` holds no files, so a repair needs the MSI as its source).
3. Run `msiexec /i <path to the MSI> REINSTALL=ALL REINSTALLMODE=amus /qn /l*v <log>` as PSAdmin; the log is saved to the evidence (`a-more-repair`).
4. Read the state and health with the pin.

**Expected:**
- `NoRepair` = 1: Programs and Features offers no Repair (`ARPNOREPAIR`).
- msiexec exit 0 (or 3010).
- `deviceId`, thumbprint, port, rule unchanged; 1 certificate; service `Running`; health answers with the old pin.
- No new "Enrolled as device" line: a repair without `ENROLLMENT` doesn't enrol.

Note: `msiexec /fa` ended with 1706 / 1603 in every run up to 20261019-193627, by product code and with a copy of the MSI alike: Windows Installer only looks in the registered source list, which holds RemoteHub's deleted upload folder. A maintenance install launched from the package (`/i`) accepts it as the source, as A-HAND-07 does.

#### A-ROLLBACK-01 · First install fails after enrolling
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** VM reverted, no agent. A way to fail after `AgentEnroll`: block TCP 58732 so the Vital service fails at `StartServices`, or use a test copy of the MSI with a failing custom action after `AgentEnroll`.

**Steps:**
1. Install with a valid enrollment and the forced failure.
2. Read the MSI log and `Get-LabAgentState`.

**Expected:**
- msiexec fails (1603); the log shows `AgentEnrollRollback` (`RemoteHubAgent.exe cleanup`) ran.
- No firewall rule, no `O=RemoteHub Agent` certificate, no `agent.json`, no service, no product.

### 5.4 Uninstall

Common setup: `A-UNINST.Tests.ps1`. `BeforeAll` reverts the VM, reads the clean state, installs with `Install-LabAgent` (defaults), reads the installed state, uninstalls with `Uninstall-LabAgent` and reads the state again. `Uninstall-LabAgent` runs, as PSAdmin, `msiexec /x <product code> /qn /norestart /l*v C:\Windows\Temp\RemoteHub-Agent-uninstall.log`, which runs `RemoteHubAgent.exe cleanup` (condition `REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE`). All states are saved as `a-uninst-state.json`; `AfterAll` copies the agent logs and the uninstall log to `a-uninst-logs`.

#### A-UNINST-01 · Installed before
P1 · How: H · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** VM reverted; agent installed by `Install-LabAgent`.

**Steps:**
1. Read the installed state before the uninstall.

**Expected:**
- Product "RemoteHub Agent" in the Uninstall key.
- Service `Running`.
- Exactly 1 `O=RemoteHub Agent` certificate.
- Firewall rule present.

Gap: the sheet says "health answers"; the test does not call `/v1/health` before the uninstall.

#### A-UNINST-02 · `msiexec /x /qn`
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** agent installed.

**Steps:**
1. `Uninstall-LabAgent`.

**Expected:**
- Exit code 0 or 3010.

#### A-UNINST-03 · Product, service, folder
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** uninstall done.

**Steps:**
1. Read the Uninstall key, `Win32_Service RemoteHubAgent` and `C:\Program Files\RemoteHub Agent`.

**Expected:**
- No product, no service, no program folder.

#### A-UNINST-04 · Firewall rule
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** uninstall done.

**Steps:**
1. `Get-NetFirewallRule -DisplayName 'RemoteHub Agent'`.

**Expected:**
- No rule.

#### A-UNINST-05 · Certificates
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** uninstall done.

**Steps:**
1. List `Cert:\LocalMachine\My` for subjects like `*O=RemoteHub Agent*`.

**Expected:**
- None.

#### A-UNINST-06 · agent.json
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** uninstall done.

**Steps:**
1. Check `C:\ProgramData\RemoteHub\Agent\agent.json`.

**Expected:**
- The file doesn't exist.

#### A-UNINST-07 · Logs kept
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** uninstall done.

**Steps:**
1. Check `C:\ProgramData\RemoteHub\Agent\logs` and read the `agent-*.log` lines.

**Expected:**
- The log folder exists.
- The log matches `Uninstalled: firewall rule, certificate.* removed` (current text: "Uninstalled: firewall rule, certificate, configuration and Ctrl+Alt+Del setting removed").

Gap: the test checks that the line exists, not that it is the last line.

#### A-UNINST-08 · Event source
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** uninstall done.

**Steps:**
1. Check `HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\RemoteHub Agent`.

**Expected:**
- The key doesn't exist.

#### A-UNINST-09 · Port
P1 · How: H · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** uninstall done; pin from the install.

**Steps:**
1. `Get-LabAgentHealth -Pin <pin>` (harness `health`, port 58732).

**Expected:**
- The call throws (no answer on 58732).

#### A-UNINST-10 · SoftwareSASGeneration
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Preconditions:** clean, installed and after-uninstall states read (value under `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`).

**Steps:**
1. Compare `SoftwareSASGeneration` in the clean state with the installed state.
2. If they are equal, skip ("agent <version> left SoftwareSASGeneration unchanged"; agents up to 0.15.5).
3. Otherwise compare the after-uninstall value with the clean value.

**Expected:**
- After the uninstall the value equals the value before the install (deleted when the agent set it: `sas.agent` marker present).

Gap: only the "agent set it" branch is tested. The "kept when a policy set it" branch (value set before the install, no marker) is not covered here (see A-INST-23).

#### A-UNINST-11 · Virtual display removed
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** agent installed with `VIRTUALDISPLAY=1` (A-INST-20); SignPath trust added by the agent.

**Steps:**
1. `Uninstall-LabAgent`.
2. List `Root\MttVDD` devices, `pnputil /enum-drivers`, `C:\VirtualDisplayDriver`, `C:\ProgramData\RemoteHub\Agent\virtual-display.txt`, TrustedPublisher.
3. Repeat with the trust present before the install (A-INST-21).

**Expected:**
- No device, no `MttVDD.inf` driver package, no `C:\VirtualDisplayDriver`, no `virtual-display.txt`.
- Thumbprint `3CF8CF26D8BA266C3A483AB7D26D4A818E317D76` removed from TrustedPublisher only when `trustAdded=1`; kept otherwise.
- Agent log: "Virtual display removed" without "(… not removed)".

#### A-UNINST-12 · Uninstall from Settings → Apps
P3 · How: S · Needs: – · Status: ✅ Automated – `A-MORE.Tests.ps1`

**Preconditions:** agent installed; Admin signed in at the console.

**Steps:**
1. Open Settings → Apps → Installed apps in Admin's session (UI Automation); UAC for admins is set to elevate without asking (the checkpoint restores it).
2. Type "RemoteHub Agent" into the page's search box (picked from the page's elements – its name differs between Windows builds), press *More options* in RemoteHub Agent's own row, choose Uninstall, confirm in the flyout, and answer Windows Installer's "Yes" where this build asks.
3. Read the device state.

Note: until 20261016 the test searched for a box named "Search apps", which wasn't found on 26H2, and then pressed the first *More options* on the page – that of whichever app came first – so RemoteHub Agent was never uninstalled.

**Expected:**
- Same results as A-UNINST-02 … A-UNINST-11.
- Only "Uninstall" is offered (the MSI sets `ARPNOMODIFY` = 1, no "Modify").

#### A-UNINST-13 · Uninstall while a desktop session is open
P2 · How: H S · Needs: – · Status: ✅ Automated – `A-MORE.Tests.ps1`

**Preconditions:** agent installed; normaluser signed in; desktop session open through the harness.

**Steps:**
1. `Uninstall-LabAgent` while the session is open.
2. Check the harness client, the processes in the user's session, the display settings and the windows.

**Expected:**
- The desktop session ends on the client.
- No `RemoteHubAgent.exe` helper left; the virtual display (if it was on) is gone and the resolution is as before; no banner window left.

#### A-UNINST-14 · Reinstall after uninstall
P2 · How: H · Needs: – · Status: ✅ Automated – `A-INSTOPT.Tests.ps1`

**Preconditions:** A-UNINST run finished (agent removed).

**Steps:**
1. `Install-LabAgent` again (new enrollment, new pin).
2. Read the state and health with the new pin.

**Expected:**
- Install succeeds; health answers with the new device ID.
- Exactly 1 `O=RemoteHub Agent` certificate and exactly 1 rule "RemoteHub Agent".

### 5.5 Configuration and service behaviour

#### A-CFG-01 · Invalid agent.json (port 0, empty groups, maxSessions 0, virtualDisplay `x`)
P2 · How: PS E · Needs: – · Status: ✅ Automated – `A-CFG.Tests.ps1`

**Preconditions:** agent installed; service stopped.

**Steps:**
1. For each case, edit `agent.json`: `listenPort` 0 / `allowedGroups` [] / `maxSessions` 0 / `virtualDisplay` "x".
2. `Start-Service RemoteHubAgent`; wait 15 s.
3. Read the service state, the agent log and `Get-NetTCPConnection -LocalPort 58732`.
4. Restore the file.

**Expected:**
- The service is not running (after its recovery restarts); nothing listens on 58732.
- The log has "RemoteHub Agent could not start:" followed by "listenPort 0 is not a valid port." / "allowedGroups is empty – nobody could connect." / "maxSessions must be at least 1." / "virtualDisplay must be \"auto\", \"always\" or \"off\", not \"x\"."
- No start line with default values.

#### A-CFG-02 · agent.json with comments / trailing commas
P3 · How: PS · Needs: – · Status: ✅ Automated – `A-CFG.Tests.ps1`

**Preconditions:** agent installed.

**Steps:**
1. Add a `// comment` line and a trailing comma to `agent.json`.
2. `Restart-Service RemoteHubAgent`.
3. Call health with the pin.

**Expected:**
- Service `Running`, health answers (the config reader skips comments and allows trailing commas).

#### A-CFG-03 · Certificate missing / key missing
P2 · How: PS E · Needs: – · Status: ✅ Automated – `A-CFG.Tests.ps1`

**Preconditions:** agent installed.

**Steps:**
1. Stop the service; delete `Cert:\LocalMachine\My\<thumbprint>`.
2. Start the service; wait 15 s.
3. Read the service state and the log.

**Expected:**
- Service not running; nothing listens on 58732.
- The log has "RemoteHub Agent could not start: Certificate <thumbprint> with private key not found in LocalMachine\My."

#### A-CFG-04 · Log rotation
P3 · How: PS · Needs: – · Status: ✅ Automated – `A-CFG.Tests.ps1`

**Preconditions:** agent installed.

**Steps:**
1. Create `agent-<date 31+ days ago>.log` files in `C:\ProgramData\RemoteHub\Agent\logs` and set their LastWriteTime to that date.
2. Restart the service (first write of the process resets the "cleaned today" flag).
3. List the folder. Repeat with `logRetentionDays` 1 and a file from 2 days ago.

**Expected:**
- Files older than `logRetentionDays` (30, then 1) are deleted at the first log write; newer ones stay.

Note: `AgentLog.Clean` deletes by file LastWriteTime, not by the date in the name. Faked names alone are not enough; the test must set LastWriteTime.

#### A-CFG-05 · Log content
P1 · How: H PS · Needs: – · Status: ✅ Automated – `A-CFG.Tests.ps1`

**Preconditions:** agent installed; pin known.

**Steps:**
1. Sign in through the agent as PSAdmin and run a marker command (for example `'RH-MARKER-7f3a'`).
2. Try normaluser (refused, 403) and PSAdmin with a wrong password (401).
3. Read the agent log and search it for the lab password and the marker text.

**Expected:**
- Lines for the sign-in, the refusals (account, source IP, reason), session start and end (PID, duration).
- The log contains neither the password nor `RH-MARKER-7f3a`.

#### A-CFG-06 · Logs not readable by users
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-CFG.Tests.ps1`

**Preconditions:** agent installed; normaluser signed in.

**Steps:**
1. In normaluser's session (`Invoke-LabInSession -Account User`) run `Get-ChildItem C:\ProgramData\RemoteHub\Agent\logs` and `Get-Content` of a log file.

**Expected:**
- Both fail with access denied (folder ACL only SYSTEM and Administrators, see A-INST-07).

#### A-CFG-07 · No outbound traffic
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-CFG.Tests.ps1`

**Preconditions:** agent installed.

**Steps:**
1. During a full scenario (health, PowerShell session, desktop session), sample `Get-NetTCPConnection -OwningProcess <agent PIDs>` every 2 s in the guest.
2. Keep connections whose local port is not the agent port.

**Expected:**
- None: every agent connection has local port 58732 (inbound only). Loopback connections to its own helper processes, if any, are listed and allowed by name.

#### A-CFG-08 · Event log writes off
P3 · How: PS E · Needs: – · Status: ✅ Automated – `A-CFG.Tests.ps1`

**Preconditions:** agent installed.

**Steps:**
1. `Set-LabAgentPolicy -Values @{ AuditToEventLog = 0 }`.
2. Cause a refused sign-in (wrong password).
3. Read the Application log for source "RemoteHub Agent" after the start time, and the agent log.

**Expected:**
- No new Application log event (for example no 2000).
- The agent log has the refusal line (the policy is read for every entry, no restart needed).

## 6. Agent – sign-in and transport security (S)

Common preconditions for this section: the VM is reverted (`Reset-LabVm`) and the agent is installed with
`Install-LabAgent` (port 58732, scope = the host's address). The harness knows the VM address and the pin. The
operator account is `PSAdmin` with the lab password. "Event" means the Application log, source "RemoteHub Agent".
"Agent log" means `%ProgramData%\RemoteHub\Agent\logs\agent-<yyyyMMdd>.log`.

#### S-TLS-01 · TLS versions
P1 · How: H · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed and healthy.

**Steps:**
1. From the host, open a TLS handshake to `<vm>:58732` with only TLS 1.2 allowed (harness probe `tls`, `tls12`).
2. Repeat with only TLS 1.3.
3. Repeat with only TLS 1.1.
4. Repeat with only TLS 1.0.

**Expected:**
- TLS 1.2: handshake succeeds, negotiated protocol `Tls12`.
- TLS 1.3: handshake succeeds, negotiated protocol `Tls13`.
- TLS 1.1 and TLS 1.0: handshake fails (`handshake = false`). Kestrel allows only `Tls12 | Tls13`.

#### S-TLS-02 · No plain HTTP
P1 · How: H · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed and healthy.

**Steps:**
1. From the host, send `GET http://<vm>:58732/v1/health` without TLS (harness probe `http`).
2. Send the same request over HTTPS with the right pin, as a control.

**Expected:**
- The plain HTTP request gets no HTTP answer (`answered = false`, connection error).
- The HTTPS control request answers 200.

#### S-TLS-03 · Pin mismatch
P1 · How: H PS E · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed. Note the time and the current Security log and agent log size.

**Steps:**
1. Open a PowerShell session through the agent with a wrong pin (any other 64-hex SHA-256) and PSAdmin's right password.
2. Repeat with the raw probe (`ws`, wrong `--pin`).
3. Read the Security log (4625, 4624 type 3) and the agent log in the guest for the time of the attempts.

**Expected:**
- RemoteHub's error: "The RemoteHub Agent on `<vm>`:58732 presented a different certificate than the one stored for this device (expected …, got …). The connection was stopped before any credentials were sent. …"
- The probe reports `pinMatched = false` and no HTTP status.
- No 4625 and no 4624 for PSAdmin in that time.
- No "Refused …" and no "Session started" line in the agent log, and no event 2000.

#### S-TLS-04 · Health reveals nothing
P1 · How: H · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Call `GET /v1/health` over pinned TLS without credentials (harness probe `health`).
2. Read the JSON field names and the response headers.

**Expected:**
- Status 200.
- JSON fields are exactly `product`, `version`, `protocol`, `deviceId`, `features`, `managed`, `instance`; `managed` only names `autoSignIn`, `virtualDisplay` or `port`; `instance` is a random 32-hex id per agent start.
- `product` = "RemoteHub Agent", `protocol` = 1, `deviceId` = the enrolled device ID.
- No `Server` header in the response (`AddServerHeader = false`).

#### S-AUTH-01 · Wrong password
P1 · How: H E · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed. No failed sign-ins from the host in the last 5 minutes.

**Steps:**
1. Open a PowerShell session through the agent as PSAdmin with a wrong password.
2. Read the Application log (source "RemoteHub Agent") and the Security log in the guest.

**Expected:**
- HTTP 401. RemoteHub says "The RemoteHub Agent on `<vm>`:58732 rejected the sign-in: wrong user name or password, …".
- Event 2000 (warning): "Refused .\PSAdmin from `<host>`: " followed by Windows' message for error 1326.
- Security event 4625 for PSAdmin, logon type 3.
- The password does not appear in the agent log or the event.

#### S-AUTH-02 · Missing / malformed Basic header
P2 · How: H · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed. No failed sign-ins from the host in the last 5 minutes.

**Steps:**
1. Send a WebSocket upgrade to `/v1/powershell` with no `Authorization` header (probe `ws --auth none`).
2. Send one with `Authorization: Basic not-base64!` (probe `--auth malformed`).
3. Repeat both 6 times in total.
4. Then sign in with the right password.

**Expected:**
- Each attempt: 401 with `WWW-Authenticate: Basic realm="RemoteHub Agent"`.
- No 429 after 6 attempts: these do not count as failed sign-ins.
- No 4625 in the Security log for these attempts.
- The final sign-in with the right password succeeds.

#### S-AUTH-03 · Not a WebSocket request
P3 · How: H · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Send a plain HTTPS `GET /v1/powershell` with valid credentials but no WebSocket upgrade (probe `ws --upgrade 0`).
2. Read the Security log.

**Expected:**
- HTTP 400.
- No 4624 or 4625 for this request: the agent checks the request type before the sign-in.

#### S-AUTH-04 · Standard user
P1 · How: H E · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed with the default allowed group S-1-5-32-544. `normaluser` is not an administrator.

**Steps:**
1. Open a PowerShell session through the agent as `normaluser` with the right password.
2. Read the Application log in the guest.

**Expected:**
- HTTP 403. RemoteHub says "The account is not allowed to use the RemoteHub Agent on `<vm>`:58732: it must be an administrator there …".
- Event 2000: "Refused .\normaluser from `<host>`: not a member of an allowed group".
- No powershell.exe started for normaluser.

#### S-AUTH-05 · User name formats
P2 · How: H · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Sign in through the agent as `PSAdmin` and run `whoami`.
2. Repeat as `.\PSAdmin`.
3. Repeat as `<COMPUTER>\PSAdmin`.
4. Repeat as `PSAdmin@<computer>`.

**Expected:**
- Steps 1 to 3: session opens, `whoami` = `<computer>\psadmin`. The agent maps `.`, empty and the computer's own name to the local account database.
- Step 4: the name is passed to Windows unchanged as a UPN (no domain). The lab records the status. It must be 200 (session) or 401, never 500.
- Note: the code does not translate `user@<computer>` to a local account. For a local account on a workgroup VM Windows will most likely refuse it (401).

#### S-AUTH-06 · Deny network logon
P1 · How: H PS · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed. In the guest, PSAdmin is given "Deny access to this computer from the network" (`SeDenyNetworkLogonRight`) with secedit.

**Steps:**
1. Open a PowerShell session through the agent as PSAdmin with the right password.
2. Open a desktop sign-in to `/v1/desktop` the same way (probe `ws --path /v1/desktop`).
3. Read the agent log and the Application log.

**Expected:**
- Both: HTTP 403. Windows refuses the network logon with error 1385, which the agent maps to "not allowed".
- Event 2000 with Windows' message for error 1385.
- No session started, no powershell.exe for PSAdmin.

#### S-AUTH-07 · Account lockout
P2 · How: H PS · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed. In the guest: `net accounts /lockoutthreshold:3`.

**Steps:**
1. Sign in through the agent as PSAdmin with a wrong password 3 times.
2. Check in the guest that Windows locked the account (Security event 4740).
3. Sign in with the right password.

**Expected:**
- Steps 1: 401 each time.
- Step 3: 401 (Windows error 1909, locked out). Not 429: only 4 failures are counted, the agent's limit is 5.
- Event 2000 with Windows' message for error 1909.

#### S-AUTH-08 · Disabled / expired account
P2 · How: H PS · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed. The test creates a local account `rhdisabled` in Administrators with the lab password.

**Steps:**
1. Disable the account (`net user rhdisabled /active:no`).
2. Sign in through the agent as `rhdisabled` with the right password.
3. Enable it again and give it an expired password (maximum password age passed).
4. Sign in again.
5. Read the Application log.

**Expected:**
- Step 2: 401, event 2000 "The account is disabled. (Windows error 1331)".
- Step 4: 401, event 2000 with the reason and the Windows error: 1330 "The password has expired.", or 1907 "The password must be changed before the account can sign in." for "User must change password at next logon" (e.g. `PasswordExpired = 1`). An expired account (1793) gets 401 "The account has expired." as well.
- Note: the agent maps every refusal of the account to 401 and only "Windows couldn't check the account" (no domain controller, broken trust, unknown errors) to 500 (`SignInRefusal.cs`, agent-spec §4.2).

#### S-AUTH-09 · Non-RID-500 admin gets a full token
P1 · How: H · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed. The test creates `lapsadmin` in Administrators. `LocalAccountTokenFilterPolicy` is not set in the guest.

**Steps:**
1. Open a PowerShell session through the agent as `lapsadmin`.
2. Run `whoami /groups`.
3. Run `New-Item HKLM:\SOFTWARE\RemoteHubLab -Force` and read it back.

**Expected:**
- Session opens.
- `whoami /groups` lists "Mandatory Label\High Mandatory Level" and BUILTIN\Administrators enabled (not "Group used for deny only").
- The HKLM key is created. The agent uses the account's full linked token.

#### S-AUTH-10 · Empty password account
P3 · How: H PS · Needs: – · Status: ✅ Automated – `S-AUTH.Tests.ps1`

**Preconditions:** Agent installed. The test creates `rhblank` in Administrators with an empty password. "Limit local account use of blank passwords to console logon only" stays at its default (on).

**Steps:**
1. Sign in through the agent as `rhblank` with an empty password.
2. Read the Application log.

**Expected:**
- HTTP 401 (Windows error 1327, account restriction).
- Event 2000 "Refused .\rhblank from `<host>`: …".

#### S-THR-01 · Throttle
P1 · How: H E · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** Agent installed. No failed sign-ins from the host in the last 5 minutes.

**Steps:**
1. Sign in through the agent as PSAdmin with a wrong password 5 times, one after another.
2. Sign in a 6th time with the right password.
3. Read the Application log.

**Expected:**
- Attempts 1 to 5: 401.
- Attempt 6: 429. RemoteHub says "The RemoteHub Agent on `<vm>`:58732 refuses sign-ins from this PC for a few minutes after too many failed attempts."
- Event 2000 "Refused `<host>`: too many failed sign-ins".
- No 4624 or 4625 for attempt 6: it never reaches Windows.

#### S-THR-02 · Parallel attempts
P1 · How: H E · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** Agent installed. No failed sign-ins from the host in the last 5 minutes. Note the Security log position.

**Steps:**
1. Send 6 sign-ins with a wrong password at the same time (probe `ws --count 6 --parallel 1`).
2. Count the 4625 events for PSAdmin since the start.

**Expected:**
- At most 5 answers are 401, the rest are 429.
- 4625 count for PSAdmin ≤ 5. Running attempts count against the limit.

#### S-THR-03 · Block ends
P2 · How: H · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** The host is blocked as in S-THR-01. Note the time of the 5th failure.

**Steps:**
1. Sign in with the right password right away.
2. Wait until 5 minutes after the 5th failure, plus 10 s.
3. Sign in with the right password again.

**Expected:**
- Step 1: 429.
- Step 3: the session opens.

#### S-THR-04 · Success clears
P3 · How: H · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** Agent installed. No failed sign-ins from the host in the last 5 minutes.

**Steps:**
1. Sign in with a wrong password 4 times.
2. Sign in with the right password once.
3. Sign in with a wrong password 4 times.

**Expected:**
- Step 1 and 3: 401 every time, never 429.
- Step 2: session opens. A successful sign-in clears the failure count.

#### S-THR-05 · Restart resets
P3 · How: H PS · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** The host is blocked as in S-THR-01.

**Steps:**
1. Restart the service in the guest (`Restart-Service RemoteHubAgent`).
2. Wait until health answers.
3. Sign in with the right password.

**Expected:**
- The session opens. The failure count is kept in memory only.

#### S-LIM-01 · Session limit
P2 · How: H · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** Agent installed. In the guest, `maxSessions` in agent.json is set to 2 and the service is restarted.

**Steps:**
1. Open 3 PowerShell sessions through the agent and keep them open (probe `hold --count 3`).
2. Close them. Open 2 PowerShell sessions and keep them open.
3. Start a desktop session (harness desktop client).
4. Read the agent log.

**Expected:**
- Step 1: sessions 1 and 2 upgrade (101), session 3 gets 503. RemoteHub's text: "… has reached its limit of simultaneous sessions."
- Step 3: 503 (PowerShell and desktop share the limit).
- Agent log: "Refused `<computer>`\PSAdmin from `<host>`: session limit (2) reached". This is a log line, not an event.

#### S-POL-01 · Unreadable policy key
P1 · How: PS H · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** Agent installed. In the guest, `HKLM\SOFTWARE\Policies\RemoteHub\Agent` exists with a Deny ACE for SYSTEM on read.

**Steps:**
1. Call health.
2. Open a PowerShell session as PSAdmin.
3. Open a desktop session as PSAdmin (probe `ws --path /v1/desktop`).
4. Read the agent log.

**Expected:**
- Health `features` empty: an unreadable key allows nothing, and since `PowerShellEnabled` health leaves out `powershell` when it's off.
- PowerShell: 404 before any sign-in – it counts as turned off, like the desktop (since `PowerShellEnabled`; 403 would mean "signed in, but no right").
- Desktop: 404 (desktop turned off).
- Agent log: "The policy (SOFTWARE\Policies\RemoteHub\Agent) can't be read – access refused: …". The line is written on connections, not on health.

#### S-POL-02 · Policy read live
P2 · How: PS H · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** Agent installed, no policy key.

**Steps:**
1. Call health. Open a PowerShell session as PSAdmin.
2. Set `DesktopEnabled` = 0 (DWORD) with `Set-LabAgentPolicy`. Do not restart the service.
3. Call health and send a desktop sign-in.
4. Set `Rights\PowerShell` to an empty local group, then open a PowerShell session.
5. Remove the policy key and repeat steps 1 and 3.

**Expected:**
- Step 1: features `powershell`, `desktop`. Session opens.
- Step 3: features `powershell` only, desktop 404.
- Step 4: 403.
- Step 5: back to the step 1 results. No service restart was needed.

#### S-POL-03 · `Rights\PowerShell`
P1 · How: PS H · Needs: – · Status: ✅ Automated – `S-THR.Tests.ps1`

**Preconditions:** Agent installed. The test creates an empty local group "RH Nobody". `Rights\PowerShell` = "RH Nobody". `Rights\Desktop` not set.

**Steps:**
1. Open a PowerShell session through the agent as PSAdmin.
2. Send a desktop sign-in as PSAdmin (probe `ws --path /v1/desktop`).
3. Read the Application log.

**Expected:**
- PowerShell: 403, event 2000 "… not a member of an allowed group".
- Desktop: WebSocket upgrade succeeds (101). `Rights\Desktop` keeps its default, the allowed groups from agent.json.

## 7. Agent – PowerShell and remote tools (P, T)

P- cases run over the agent. Each T- case runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01), through
RemoteHub's own classes in the harness (`RemoteFs`, `FileUploader`/`FileDownloader`, `RemoteRegistry`,
`RemoteServices`, `RemoteProcesses`). Each harness call opens its own session. The result is checked in the guest by
PowerShell Direct.

Common preconditions: VM reverted, agent installed with `Install-LabAgent`, operator `PSAdmin`. For the WinRM run:
the setup script ran with local administrators allowed (`LocalAccountTokenFilterPolicy` = 1, PSAdmin is not RID 500)
and the certificate is trusted on the host (W-HTTPS-04). Test files live under `C:\LabT` in the guest.

#### P-SES-01 · Session
P1 · How: H E · Needs: – · Status: ✅ Automated – `P-SES.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Run "Test PowerShell remoting" through the agent (harness `test --via agent`).
2. Run `$PSVersionTable.PSVersion.ToString(); whoami` in a session (harness `ps`).
3. Read event 1000 in the Application log.
4. Check the PID from the event in the guest while a session is open.

**Expected:**
- Test: state `Available`, message "PowerShell 5.1 on `<COMPUTERNAME>`", transport "RemoteHub Agent".
- Version starts with 5.1. `whoami` = `<computer>\psadmin`.
- Event 1000: "Session started: `<COMPUTER>`\PSAdmin from `<host>` (PowerShell PID `<n>`)".
- PID `<n>` is `powershell.exe` run as PSAdmin with `-Version 5.1 -s -NoLogo -NoProfile`.

#### P-SES-02 · Profile + environment
P2 · How: H · Needs: – · Status: ✅ Automated – `P-SES.Tests.ps1`

**Preconditions:** Agent installed. PSAdmin has signed in once (profile exists).

**Steps:**
1. Run `$env:USERPROFILE; $env:USERNAME; $env:TEMP` through the agent.
2. Run `New-Item HKCU:\Software\RemoteHubLab -Force | Out-Null; Set-ItemProperty HKCU:\Software\RemoteHubLab -Name T -Value 1; (Get-ItemProperty HKCU:\Software\RemoteHubLab).T`.
3. In the guest, load PSAdmin's hive and read the value.

**Expected:**
- `USERPROFILE` = `C:\Users\PSAdmin`, `USERNAME` = `PSAdmin`, `TEMP` below PSAdmin's profile.
- The value reads back as 1 and is in PSAdmin's `NTUSER.DAT`. The agent loads the user's profile.

#### P-SES-03 · End kills the tree
P1 · How: H PS E · Needs: – · Status: ✅ Automated – `P-SES.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Through the agent, run a script that starts `cmd.exe /k` (a window) and `ping.exe -t localhost` with `Start-Process -PassThru` and returns their PIDs and `$PID`. Not `notepad.exe`: on Windows 11 it hands over to the packaged Notepad, which the app activation starts outside the session's process tree (run 20261019-193627).
2. The harness call ends and closes the session.
3. In the guest, poll for the three PIDs for up to 5 s.
4. Read event 1001.

**Expected:**
- powershell.exe, cmd.exe and ping.exe are gone within 5 s (job object with kill on close).
- Event 1001: "Session ended: `<COMPUTER>`\PSAdmin from `<host>` after `<hh:mm:ss>` (closed by RemoteHub)".

#### P-SES-04 · Large arguments
P2 · How: H · Needs: – · Status: ✅ Automated – `P-SES2.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Run a script with a 2 MB string literal that returns its length and SHA-256.
2. Run `'x' * 10MB` and return the result.
3. Compare length and hash on the host.

**Expected:**
- Step 1: length 2097152 (or the exact length sent) and the same SHA-256 as on the host.
- Step 2: a 10485760-character string arrives complete.
- No errors. The session stays usable for a following command.

#### P-SES-05 · Message > 16 MiB
P3 · How: H · Needs: – · Status: ✅ Automated – `P-SES2.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Open a PowerShell WebSocket to the agent and send one text message of 17 MiB.
2. Read event 1001 and the agent log.
3. Call health and open a new session.

**Expected:**
- The session ends. Event 1001 reason: "connection lost: Message too large."
- Health answers 200 and a new session works.
- Note: RemoteHub's client sends fragments of 1 MiB, so it never sends such a message. The harness probe `big --mb 17` sends one raw message in 1 MiB frames.

#### P-SES-06 · Network drop
P2 · How: N PS E · Needs: – · Status: ✅ Automated – `P-SES2.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Open a PowerShell session through the agent and hold it (probe `hold --seconds 180`).
2. Note the session's powershell.exe PID in the guest.
3. Cut the VM's network (`Disconnect-VMNetworkAdapter`).
4. Poll the PID in the guest through PowerShell Direct for up to 90 s.
5. Restore the network. Read event 1001.

**Expected:**
- powershell.exe is gone within about 60 s (keep-alive ping every 30 s, 30 s timeout).
- Event 1001 with reason "connection lost: …".
- The agent answers health after the network is back.

#### P-SES-07 · 20 sessions in parallel
P3 · How: H · Needs: – · Status: ✅ Automated – `P-SES.Tests.ps1`

**Preconditions:** Agent installed with the default `maxSessions` 20.

**Steps:**
1. Open 21 PowerShell sessions through the agent and keep them open (probe `hold --count 21`).
2. While they are open, count powershell.exe processes for PSAdmin in the guest.
3. Read the agent log.

**Expected:**
- Sessions 1 to 20 upgrade (101), session 21 gets 503.
- 20 powershell.exe processes for PSAdmin.
- Agent log: "… session limit (20) reached".

#### P-SES-08 · Read-Host / Get-Credential prompts
P3 · How: H · Needs: – · Status: ✅ Automated – `P-SES2.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Run `Read-Host 'Name'` through the agent with a host UI that answers "lab".
2. Run `(Get-Credential -UserName x -Message m).UserName` with a host UI that answers a credential.

**Expected:**
- Step 1 returns "lab".
- Step 2 returns "x".
- Note: the harness documents `ps --host-ui 1` but does not implement it: it opens the runspace without a PSHost, so Read-Host fails. The answering host UI (like `PsConsoleHost`) has to be added to the harness first.

#### P-SES-09 · Tab completion
P3 · How: H · Needs: – · Status: ✅ Automated – `P-SES.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. Complete `Get-Ser` (harness `complete`).
2. Complete `Get-Service -Na`.
3. Complete `dir C:\Win`.

**Expected:**
- Step 1: matches include `Get-Service`.
- Step 2: matches include `-Name`.
- Step 3: matches include `C:\Windows`.

#### P-CLM-01 · Constrained Language Mode
P2 · How: H · Needs: CLM · Status: ✅ Automated – `P-SES2.Tests.ps1`

**Preconditions:** CLM VM (WDAC policy forcing Constrained Language Mode). Agent installed.

**Steps:**
1. Open a session through the agent and run `$ExecutionContext.SessionState.LanguageMode`.
2. List `C:\` with `RemoteFs`.
3. Rename a test registry key with `RemoteRegistry.RenameKey` (uses `Add-Type`).
4. List services and processes.

**Expected:**
- Session opens, language mode `ConstrainedLanguage`.
- Each tool either works or fails with a readable message (`refused` in the harness), never a hang or crash.
- The key rename fails with PowerShell's error about `Add-Type` in this language mode.
- Note: the code has no Constrained Language Mode check of its own. The "clear report" is PowerShell's error text passed on as `RemoteCommandException`.

#### T-FS-01 · List drives / folders
P2 · How: H · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. In the guest, `C:\LabT\hidden.txt` with the Hidden attribute. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. List "" (harness `fs-list --path ""`).
2. List `C:\LabT`.
3. List `%TEMP%`.

**Expected:**
- Step 1: path "", entries of kind `Drive`, including `C:\`.
- Step 2: `hidden.txt` with `Hidden = true`.
- Step 3: the path is expanded on the remote side to PSAdmin's temp folder (`C:\Users\PSAdmin\AppData\Local\Temp`), not the host's.
- A missing folder gives "Folder not found: `<path>`".

#### T-FS-02 · Upload small files
P1 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. 300 random files of 1 to 900 KB on the host. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Upload them to `C:\LabT\small` (harness `fs-upload`).
2. In the guest, hash every file (SHA-256).
3. Search `C:\LabT\small` for `*.remotehub-part`.

**Expected:**
- `done` = 300, `failed` = 0. Each status ends with "SHA-256 verified".
- All hashes match the host.
- No `.remotehub-part` file.

#### T-FS-03 · Upload large file
P1 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. On the host: `big.txt` (200 MB of text) and `random.bin` (200 MB random). Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Upload both to `C:\LabT\big`.
2. In the guest, read the size and SHA-256 of both files.
3. Search for `*.remotehub-part`.

**Expected:**
- Both `Done`. Hashes and sizes (209715200 bytes) equal the host's.
- `big.txt` status contains "sent compressed (`<n>`%)". `random.bin` status does not.
- The files in the guest are not gzip data (size and hash equal the originals).
- No `.remotehub-part` file.

#### T-FS-04 · Upload folder tree
P2 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS2.Tests.ps1`

**Preconditions:** Common. On the host, a folder `tree` with nested folders, names like `Ünïcödé 文件`, and one file whose target path is longer than 260 characters. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Upload the folder `tree` to `C:\LabT`.
2. In the guest, list all files below `C:\LabT\tree` with full paths and hashes.

**Expected:**
- The tree arrives as `C:\LabT\tree\…` with the same relative paths and names.
- All hashes match.
- Note: the remote side uses .NET Framework file calls in Windows PowerShell 5.1 without `\\?\` paths. Paths over 260 characters only work if `LongPathsEnabled` = 1 in the guest. The case must set it or expect a "Failed – …" item for that file.

#### T-FS-05 · Skip existing / overwrite
P2 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. `C:\LabT\a.txt` (small) and `C:\LabT\b.bin` (5 MB) already exist in the guest with other content. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Upload `a.txt`, `b.bin` and a new `c.txt` without overwrite.
2. Hash the guest files.
3. Upload the same files with overwrite (`--overwrite 1`).
4. Hash again.

**Expected:**
- Step 1: `a.txt` and `b.bin` `Skipped` with "Skipped – the file already exists". `c.txt` `Done`. Guest content of a and b unchanged.
- Step 3: all three `Done`. Hashes now equal the host's.

#### T-FS-06 · Cancel upload
P1 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. A 200 MB file on the host. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Upload it to `C:\LabT\cancel` and cancel after 50 MB (`--cancel-after-mb 50`).
2. In the guest, look for the target file and `*.remotehub-part`.

**Expected:**
- The item is `Cancelled` with status "Cancelled".
- No target file and no `.remotehub-part` in `C:\LabT\cancel`.

#### T-FS-07 · Download
P1 · How: H · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. In the guest, `C:\LabT\dl\one.bin` (20 MB) and a folder `C:\LabT\dl\sub` with 50 files. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Download `C:\LabT\dl\one.bin` and `C:\LabT\dl\sub` to an empty host folder (harness `fs-download`).
2. Hash the files in the guest through PowerShell Direct.
3. Try to download `C:\`.

**Expected:**
- All items `Done` with "SHA-256 verified". Host hashes equal the guest's. Folder files keep their `sub\…` relative paths.
- No `.remotehub-part` left on the host.
- Step 3: refused with "A whole drive cannot be downloaded: C:\".

#### T-FS-08 · Rename / delete / new folder
P2 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Create `C:\LabT\New` (harness `fs-mkdir`).
2. Rename it to `Renamed` (`fs-rename`).
3. Delete `C:\LabT\Renamed` with a file inside (`fs-delete`).
4. Delete `C:\`.
5. Check each step in the guest.

**Expected:**
- Steps 1 to 3 done in the guest. The rename returns `C:\LabT\Renamed`.
- Step 3 removes the folder and its content.
- Step 4: refused with "Refusing to delete a drive root: C:\". Nothing is deleted.

#### T-FS-09 · Locked file
P3 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS2.Tests.ps1`

**Preconditions:** Common. In the guest, `C:\LabT\lock\small.txt` (100 KB) and `big.bin` (5 MB), each held open with `FileShare.None` by a PowerShell Direct session. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Upload new versions of both files with overwrite.
2. Release the locks. Hash the guest files.
3. Look for `*.remotehub-part`.

**Expected:**
- Both items `Failed` with "Failed – " and Windows' "being used by another process" message.
- Both originals unchanged.
- No `.remotehub-part` file.
- Note: for files over 1 MiB the script deletes the original before the rename. When that delete fails, `Drop` does not remove the part file (its stream is already closed). By the code, `big.bin.remotehub-part` is left behind.

#### T-FS-10 · Defender real-time on
P3 · How: H · Needs: – · Status: ✅ Automated – `T-TOOLS2.Tests.ps1`

**Preconditions:** Common. In the guest, `Get-MpComputerStatus` shows real-time protection on. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Upload a tree of 1000 small files and two 200 MB files to `C:\LabT\av`.
2. Hash all files in the guest.

**Expected:**
- All items `Done` with "SHA-256 verified". None `Failed`.
- All hashes match. No `.remotehub-part` left.

#### T-REG-01 · Browse
P2 · How: H · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. List `""` (harness `reg-list`).
2. List `HKLM\SOFTWARE`, `HKLM:\SOFTWARE`, `Registry::HKEY_LOCAL_MACHINE\SOFTWARE`, `Computer\HKEY_LOCAL_MACHINE\SOFTWARE`.
3. List `HKXX\Foo`.

**Expected:**
- Step 1: the five hives.
- Step 2: each answer has path `HKEY_LOCAL_MACHINE\SOFTWARE` and the same sub keys (64-bit view: `Microsoft`, `Policies`, `WOW6432Node`, …).
- Step 3: refused with "Unknown registry hive "HKXX". Use e.g. HKLM\SOFTWARE or HKEY_CURRENT_USER\Software."

#### T-REG-02 · Value types
P2 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. Key `HKLM\SOFTWARE\RemoteHubLab` exists. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Create one value per type (`reg-set`): String "a", ExpandString `%SystemRoot%\x`, MultiString "l1\nl2", DWord 0xFFFFFFFF, QWord 18446744073709551615, Binary `01 a0 ff`.
2. Check each with `reg query HKLM\SOFTWARE\RemoteHubLab` in the guest.
3. List the key with `reg-list`. Change each value and check again.
4. Delete each value (`reg-delete-value`) and check with `reg query`.

**Expected:**
- Types REG_SZ, REG_EXPAND_SZ, REG_MULTI_SZ, REG_DWORD, REG_QWORD, REG_BINARY as written.
- ExpandString stays `%SystemRoot%\x` (not expanded) in `reg query` and in the list.
- List data: DWORD "4294967295", QWORD "18446744073709551615", Binary "01 A0 FF", MultiString lines "l1" and "l2".
- After step 4 the values are gone.

#### T-REG-03 · Key rename keeps ACL
P3 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS2.Tests.ps1`

**Preconditions:** Common. In the guest, `HKLM\SOFTWARE\RemoteHubLab\AclKey` with an extra ACE (normaluser full control). SDDL recorded. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Rename the key to `AclKey2` (harness `reg-rename-key`).
2. In the guest, read the SDDL of `AclKey2`.
3. Rename `AclKey2` to a name that already exists.

**Expected:**
- `AclKey` is gone, `AclKey2` exists with its values and sub keys.
- SDDL equals the recorded one (RegRenameKey, no copy).
- Step 3: refused with "A key named '`<name>`' already exists."

#### T-REG-04 · Export
P3 · How: H · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. `HKLM\SOFTWARE\RemoteHubLab` with values from T-REG-02. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Export the key to a host file (harness `reg-export`).
2. In the guest, run `reg export HKLM\SOFTWARE\RemoteHubLab C:\LabT\ref.reg /y` and copy the file to the host.
3. Compare both files byte by byte.
4. Export `HKLM` alone.

**Expected:**
- The files are equal.
- Step 4: refused with "Choose a key below the hive to export."

#### T-REG-05 · Hive protection
P1 · How: H · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Delete `HKLM\SOFTWARE` (harness `reg-delete-key`).
2. Rename `HKLM\SOFTWARE` (`reg-rename-key`).
3. Delete `HKLM`.

**Expected:**
- Step 1: refused "Refusing to delete a top-level key: HKEY_LOCAL_MACHINE\SOFTWARE".
- Step 2: refused "Refusing to rename a top-level key: HKEY_LOCAL_MACHINE\SOFTWARE".
- Step 3: refused "Refusing to delete a top-level key: HKEY_LOCAL_MACHINE".
- The refusal happens before anything is sent. `HKLM\SOFTWARE` is unchanged.

#### T-SVC-01 · List
P2 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. List services (harness `svc-list`).
2. In the guest, run `Get-CimInstance Win32_Service`.
3. Compare per service.

**Expected:**
- Same set of names.
- State, account (`StartName`) and PID match.
- Start mode maps: Auto → `Automatic`, Auto with `DelayedAutoStart` → `AutomaticDelayed`, Manual → `Manual`, Disabled → `Disabled`, Boot/System → `Other`.
- `RemoteHubAgent` shows `AutomaticDelayed`, account `LocalSystem`.

#### T-SVC-02 · Start / stop / restart
P2 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. `Spooler` running. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Stop `Spooler` (harness `svc-control --action Stop`) and check in the guest.
2. Start it and check.
3. Restart it and compare its PID.
4. Set it to Disabled in the guest, then start it.

**Expected:**
- Steps 1 to 3 each return within 60 s. Status Stopped, Running, Running with a new PID.
- Step 4: refused "Print Spooler is disabled – change its startup type first."

#### T-SVC-03 · Startup type
P2 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. Test service `Spooler`. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. Set startup to `Automatic`, `AutomaticDelayed`, `Manual`, `Disabled` in turn (harness `svc-startup`).
2. After each, read `StartMode` and `DelayedAutoStart` in the guest.
3. After each, list services with `svc-list`.

**Expected:**
- Guest: Auto/False, Auto/True, Manual, Disabled.
- `svc-list` shows the same startup type.

#### T-PROC-01 · List
P2 · How: H · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. Admin is signed in at the console. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. List processes (harness `proc-list`).
2. Find the entry with `Id` = `OwnPid`.
3. Find `explorer.exe` of the console session.

**Expected:**
- `OwnPid` is in the list: `powershell.exe` over the agent, `wsmprovhost.exe` over WinRM, user `<COMPUTER>\PSAdmin`.
- `explorer.exe` shows user `<COMPUTER>\Admin`. User names are filled for other users' processes.
- `Cpus` equals the VM's logical processor count.

#### T-PROC-02 · End process / tree
P2 · How: H PS · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. In the guest, a test tree: `cmd.exe` → `cmd.exe` → `ping.exe -t localhost`, plus a lone `notepad.exe`. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. End `notepad.exe` without tree (harness `proc-stop`).
2. End the top `cmd.exe` with `--tree 1`.
3. Check all PIDs in the guest.

**Expected:**
- notepad.exe is gone.
- The top cmd.exe, its child and ping.exe are all gone. No "Could not end: …" error.

#### T-PROC-03 · Critical processes
P1 · How: H · Needs: – · Status: ✅ Automated – `T-TOOLS.Tests.ps1`

**Preconditions:** Common. Runs twice: over the agent and over WinRM HTTPS (after W-HTTPS-01).

**Steps:**
1. List processes and pick the PIDs of `lsass.exe`, `csrss.exe`, `winlogon.exe`.
2. End each of them (harness `proc-stop` with name and PID).
3. End the session's own remoting process.
4. Check that the VM still runs.

**Expected:**
- Step 2: refused "`<name>` (PID `<n>`) is a critical Windows process and can't be ended from here." Nothing is sent to the VM.
- Step 3: refused "This is the PowerShell remoting process of this tab."
- The processes are still running.
- Note: each harness call opens a new session, so the `OwnPid` from `proc-list` is not the process of the next call. Step 3 needs one session for list and stop (a new harness op).

### 7.2 Running scripts (S-SCRIPT)

**Preconditions (all):** the harness runs scripts through RemoteHub's `ScriptRunner` (`Invoke-LabScriptRun`). S-SCRIPT-01 … 06: the agent installed, the standard user signed in on the console (`Initialize-LabVm -Agent -AutoLogon User`, `Wait-LabDesktop -Account User`). S-SCRIPT-07 … 09: from the checkpoint, WinRM HTTPS set up with RemoteHub's script, nobody signed in. Leftovers are read with `Get-LabScriptLeftovers` (tasks under `\RemoteHub\`, folders under `%ProgramData%\RemoteHub\Runs`).

#### S-SCRIPT-01 · System run (agent)
P1 · How: H PS · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:**
1. Run `whoami; [Environment]::Is64BitProcess` as System over the agent.
2. Read the leftovers over PowerShell Direct.

**Expected:**
- *Succeeded*, exit code 0; the output has `nt authority\system` and `True`.
- No scheduled task and no run folder left.

#### S-SCRIPT-02 · User run (agent)
P1 · How: H PS · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:**
1. `script-info`: who is signed in.
2. Run as User: `whoami`, the `USERNAME` of `HKCU:\Volatile Environment`, and a value `RemoteHubLab = 42` written under `HKCU:\Software`.
3. Read `HKEY_USERS\<the user's SID>\Software\RemoteHubLab` over PowerShell Direct.

**Expected:**
- Step 1: exactly one console user, the standard user.
- Step 2: *Succeeded*; the output names the standard user and not SYSTEM.
- Step 3: 42 – the script wrote to the user's own hive. No task left.

#### S-SCRIPT-03 · As me (agent)
P2 · How: H · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:** run `whoami` as As me.

**Expected:** *Succeeded*; the output names the connection's account (PSAdmin).

#### S-SCRIPT-04 · Exit codes and parameters
P2 · How: H · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:**
1. Run `exit 3010`.
2. Run `Write-Output 'before'; throw 'lab failure'`.
3. Run `param([string]$Name, [int]$Days, [switch]$Apply) "$Name/$Days/$Apply"` with `Name = SRV 01`, `Days = 45`, `Apply` on.

**Expected:**
- Step 1: *Succeeded – restart needed*.
- Step 2: *Failed*, exit code 1, "lab failure" in the output.
- Step 3: the output has `SRV 01/45/True`.

#### S-SCRIPT-05 · Cancel
P1 · How: H PS · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:**
1. Run `Write-Output 'started'; Start-Sleep -Seconds 600` as System; cancel 5 s after the first output.
2. Read the leftovers and the processes over PowerShell Direct.

**Expected:**
- *Cancelled*, the call returns within 2 minutes.
- No task, no run folder, no powershell.exe with `RemoteHub\Runs` in its command line.

#### S-SCRIPT-06 · Large output
P3 · How: H · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:** run a script writing 40,000 lines of 60 characters (about 2.4 MB).

**Expected:** *Succeeded*; the output is cut and ends with "Output cut off after 1 MB".

#### S-SCRIPT-07 · System run (WinRM)
P1 · How: H PS · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:** as S-SCRIPT-01, over WinRM HTTPS.

**Expected:** *Succeeded*, `nt authority\system`; no task and no run folder left.

#### S-SCRIPT-08 · User run with nobody signed in
P2 · How: H · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:** run `whoami` as User over WinRM with nobody signed in.

**Expected:** refused: "No user is signed in on the device."

#### S-SCRIPT-09 · GPO-set execution policy
P1 · How: H PS · Needs: – · Status: ✅ Automated – `S-SCRIPT.Tests.ps1`

**Steps:**
1. Set `EnableScripts = 1` and `ExecutionPolicy = AllSigned` under `HKLM\SOFTWARE\Policies\Microsoft\Windows\PowerShell` over PowerShell Direct.
2. Run `whoami` as System over WinRM.
3. Remove the policy again.

**Expected:**
- Step 2: refused: "The device's group policy only allows signed scripts." No task created – RemoteHub doesn't try another way around the policy.

### 7.3 Device actions (T-DEV)

**Preconditions (all):** the harness runs the actions through RemoteHub's `DeviceActions` (`Invoke-LabTool -Op dev-users` / `dev-action`), as a connection's Device actions do. Once over the agent (`Initialize-LabVm -Agent -AutoLogon User`), once from the checkpoint over WinRM HTTPS set up with RemoteHub's script and the standard user signed in automatically (`Set-LabAutoLogon -Account User`). Sessions are read over PowerShell Direct as WTS lists them (`Get-LabSessions`). The cases run in this order in each pass: T-DEV-08 and 09 restart and shut the device down.

#### T-DEV-01 · Who is signed in
P2 · How: H PS · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:** ask who is signed in; read the sessions over PowerShell Direct.

**Expected:**
- The console user is listed once, with the id of the console session that has their explorer.exe.
- No `DWM-n` or `UMFD-n` account and nothing in session 0 (the remoting session itself).

#### T-DEV-02 · Send message
P2 · How: H C · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:**
1. Send "RemoteHub lab message Bluebird <transport>".
2. Take screenshots of the console until the word is read.

**Expected:**
- Answered "Message shown on <device> to <user>."
- "Bluebird" on the console within 60 s.

#### T-DEV-03 · Lock
P1 · How: H PS · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:** lock; read the sessions.

**Expected:**
- Answered "Locked on <device>: <user>."
- The user's session is *Disconnected* within 60 s, with the same explorer.exe as before (their programs keep running).
- The console session shows the sign-in screen (LogonUI).

#### T-DEV-04 · Sign out
P1 · How: H PS · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:** sign out (the session is still disconnected from T-DEV-03); read the sessions.

**Expected:**
- Answered "Signed out on <device>: <user>."
- Within 90 s the old session and its explorer.exe are gone. (Automatic sign-in may start a new session for the user; that one doesn't count.)

#### T-DEV-05 · Update Group Policy
P2 · How: H PS E · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:** note the guest's time; update Group Policy; read `Microsoft-Windows-GroupPolicy/Operational`.

**Expected:**
- Answered "Group Policy was updated on <device>." with gpupdate's own text.
- A manual policy run (event 4004 or 4005) after the noted time.

#### T-DEV-06 · Sync with Intune
P3 · How: H PS · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:** read whether the device has MDM enrollment tasks (`\Microsoft\Windows\EnterpriseMgmt\*`); sync.

**Expected:**
- Without an enrollment (the lab's VMs): not done, "<device> isn't enrolled in Intune or another MDM".
- With one: done, the enrollment's sync task started.

Note: the lab VMs aren't enrolled, so only the first bullet runs.

#### T-DEV-07 · No administrator's rights (agent)
P2 · How: H PS · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:**
1. Create the local standard user `rhdevstd`; give Administrators and Users the agent's PowerShell right (`Rights\PowerShell`).
2. Ask who is signed in as `rhdevstd`.

**Expected:** refused with "RemoteHub can't see who is signed in: the account isn't an administrator on the device" – never "nobody is signed in", which would make Lock and Sign out look done.

#### T-DEV-08 · Restart
P1 · How: H PS E · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:** note the boot time and guest time; restart; wait for the VM.

**Expected:**
- Done; a new boot time within 10 min.
- Event 1074 in the System log after the noted time, with "Requested from RemoteHub".
- Over the agent: the agent answers again with its pin.

#### T-DEV-09 · Shut down
P1 · How: H · Needs: – · Status: ◐ Written – `T-DEV.Tests.ps1`

**Steps:** shut down; read the VM's state on the host.

**Expected:** done; the VM is off (EnabledState 3) within 5 min.

## 8. WinRM setup script (W)

The script is generated by `RemotingSetup.BuildScript` (harness `script --host <vm> --allow <host>`, local
administrators allowed unless the case says otherwise) and run elevated in the guest by PowerShell Direct as PSAdmin
(`Invoke-LabSetupScript`). Then RemoteHub's own remoting code connects from the host. Common precondition: VM
reverted, WinRM HTTPS not set up in the checkpoint.

#### W-HTTPS-01 · HTTPS setup
P1 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** Common. Script for host = the VM's IPv4, allow = the host's address.

**Steps:**
1. Generate the script and copy it to the guest.
2. Run it elevated in the guest and capture all output streams.
3. List `WSMan:\localhost\Listener`.

**Expected:**
- No error records. Output contains "Done. PowerShell remoting is enabled over HTTPS (5986) for `<host>`".
- Output contains "Certificate thumbprint:  " followed by 10 groups of 4 hex characters.
- One listener with `Transport=HTTPS`, port 5986, `CertificateThumbprint` = the printed thumbprint without spaces.
- WinRM service running, startup Automatic.

#### W-HTTPS-02 · Certificate
P1 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** W-HTTPS-01 done.

**Steps:**
1. Read the certificate with the printed thumbprint from `Cert:\LocalMachine\My`.
2. Read its extensions, key and dates.
3. On a fresh revert, run the script with the computer name as host and read the SAN again.

**Expected:**
- FriendlyName "WinRM HTTPS (RemoteHub)", subject `CN=<vm ip>`.
- Basic constraints: not a CA (`ca=0`, critical). EKU only Server Authentication (1.3.6.1.5.5.7.3.1).
- RSA 3072, SHA256, key usage DigitalSignature and KeyEncipherment, private key not exportable.
- SAN `IP Address=<vm ip>`. Step 3: SAN `DNS=<name>`.
- NotAfter about 2 years after the run (± 1 day).

#### W-HTTPS-03 · Fetched certificate matches
P1 · How: H · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** W-HTTPS-01 done.

**Steps:**
1. On the host, fetch the listener's certificate with `FetchCertificateAsync` on port 5986 (harness `trust`).
2. Compare its thumbprint with the one the script printed.

**Expected:**
- The thumbprints are equal. Subject `CN=<vm ip>`.
- Note: the harness has no fetch-only op. `trust` fetches and also adds the certificate to the lab account's CurrentUser\Root (see W-HTTPS-04).

#### W-HTTPS-04 · Session pinned
P1 · How: H · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** W-HTTPS-01 done.

**Steps:**
1. Before trusting, run "Test PowerShell remoting" over WinRM HTTPS as PSAdmin (harness `test --via winrm`).
2. Trust the certificate (harness `trust`).
3. Run the test again.
4. Run `whoami` in a session (harness `ps --via winrm`).

**Expected:**
- Step 1: `Unavailable` with "The HTTPS certificate of the computer is not trusted by this PC, or its name does not match the host name."
- Step 2: CurrentUser\Root of the lab account has the certificate with FriendlyName "RemoteHub – WinRM `<vm ip>`".
- Step 3: `Available`, "PowerShell 5.1 on `<COMPUTERNAME>`", transport "PowerShell remoting (HTTPS)".
- Step 4: `<computer>\psadmin`.
- Note: WinRM has no thumbprint pin in the code. Trust comes from CurrentUser\Root. Adding to that store shows a Windows prompt. In the non-interactive `labop` task no one can confirm it, so check that `trust` works there.

#### W-HTTPS-05 · Firewall
P1 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** W-HTTPS-01 done. Before the run, the built-in `WINRM-HTTP-In-TCP*` rules were off.

**Steps:**
1. Read rule `RemoteHub-WinRM-HTTPS-In` with its port and address filters.
2. Read the built-in `WINRM-HTTP-In-TCP*` rules.

**Expected:**
- `RemoteHub-WinRM-HTTPS-In` exists: display name "WinRM HTTPS (RemoteHub)", inbound, allow, TCP 5986, remote address = exactly the allowed addresses, profile Any.
- All `WINRM-HTTP-In-TCP*` rules are disabled.

#### W-HTTPS-06 · Reject from elsewhere
P2 · How: N · Needs: – · Status: ✅ Automated – `W-SETUP2.Tests.ps1`

**Preconditions:** W-HTTPS-01 done with only the host's address allowed. A second source address (second NIC on the host or another lab VM).

**Steps:**
1. Connect to `<vm>:5986` from the allowed address.
2. Connect to `<vm>:5986` from the other address.

**Expected:**
- Step 1: TCP connects and the test succeeds.
- Step 2: TCP connect times out. RemoteHub's text: "No WinRM service answered on `<vm>` (HTTPS 5986). …"
- Note: the harness can't pick a source address. This needs a second NIC or a second VM.

#### W-TWICE-01 · Run twice
P2 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** W-HTTPS-01 done. Thumbprint recorded.

**Steps:**
1. Generate the script again and run it a second time.
2. Read the printed thumbprint, the listeners, the certificates and the rules.

**Expected:**
- Same thumbprint (the certificate is reused while it is valid more than 30 days).
- One certificate with FriendlyName "WinRM HTTPS (RemoteHub)". One HTTPS listener. One rule `RemoteHub-WinRM-HTTPS-In`.
- The second run prints "An HTTPS listener already exists - its certificate is replaced."

#### W-PRE-01 · Built-in rule on before
P2 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** Common. Before the run: `Enable-NetFirewallRule -Name WINRM-HTTP-In-TCP`.

**Steps:**
1. Run the script.
2. Read the state of `WINRM-HTTP-In-TCP` and the warnings in the output.

**Expected:**
- `WINRM-HTTP-In-TCP` is still enabled.
- Warning "Windows' own WinRM rule was already on before (WINRM-HTTP-In-TCP) - left as it is."
- Other `WINRM-HTTP-In-TCP*` rules that were off (e.g. `-PUBLIC`) are off again.

#### W-LISTENER-01 · Existing HTTPS listener
P2 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** Common. Before the run: WinRM enabled and an HTTPS listener bound to another self-signed certificate.

**Steps:**
1. Run the script.
2. List the HTTPS listeners and their thumbprints.

**Expected:**
- Warning "An HTTPS listener already exists - its certificate is replaced."
- Still one HTTPS listener. Its `CertificateThumbprint` is the new RemoteHub certificate.
- Note: the script changes the certificate of the existing listener. It does not delete and recreate it, so other listener settings (e.g. its address) stay.

#### W-DRY-01 · `$DryRun = $true`
P2 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** Common. Snapshot in the guest: WSMan listeners, firewall rules (`RemoteHub-*`, `WINRM-HTTP-In-TCP*` with enabled state), `Cert:\LocalMachine\My`, `LocalAccountTokenFilterPolicy`, WinRM service state and startup.

**Steps:**
1. Generate the script and change the line `$DryRun = $false` to `$DryRun = $true`.
2. Run it.
3. Take the snapshot again and compare.

**Expected:**
- Every step prints "WOULD: …". No "Done." line and no thumbprint.
- Both snapshots are equal.

#### W-LEGACY-01 · Old rule name
P3 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** Common. Before the run: a rule named `RdpManager-WinRM-HTTPS-In` (inbound, TCP 5986).

**Steps:**
1. Run the script.
2. Read the firewall rules by name.

**Expected:**
- `RdpManager-WinRM-HTTPS-In` is gone.
- Exactly one rule `RemoteHub-WinRM-HTTPS-In`.

#### W-LATFP-01 · LocalAccountTokenFilterPolicy option
P2 · How: PS · Needs: – · Status: ✅ Automated – `W-SETUP.Tests.ps1`

**Preconditions:** Common. `LocalAccountTokenFilterPolicy` not set before.

**Steps:**
1. Generate and run the script with local administrators allowed (`--local-admins 1`).
2. Read `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\LocalAccountTokenFilterPolicy`.
3. Revert. Generate and run the script with `--local-admins 0`.
4. Read the value again.

**Expected:**
- Step 2: DWORD 1.
- Step 4: value not set. Output has the warning "Local administrators are restricted over the network here (LocalAccountTokenFilterPolicy is not 1): …".
- The script from step 3 has no "LocalAccountTokenFilterPolicy = 1" step.

#### W-NONADMIN-01 · Non-RID-500 admin over WinRM without LATFP
P3 · How: H · Needs: – · Status: ✅ Automated – `W-SETUP2.Tests.ps1`

**Preconditions:** Script run with `--local-admins 0`, certificate trusted (W-HTTPS-04). The test creates `lapsadmin` in Administrators.

**Steps:**
1. Run "Test PowerShell remoting" over WinRM HTTPS as `lapsadmin`.
2. Run the same test through the agent as `lapsadmin` (S-AUTH-09).

**Expected:**
- Step 1: `Unavailable` with RemoteHub's text "Access denied. Either the user name or password is wrong, or the account signs in but isn't allowed: …".
- Step 2: session works with a full token.
- Note: WinRM's default endpoint refuses a filtered admin token. So the result is "access denied", not a session with a filtered token.

#### W-HTTP-01 · HTTP + Kerberos by name
P2 · How: H PS · Needs: Domain · Status: ✅ Automated – `W-SETUP2.Tests.ps1`

**Preconditions:** Domain VM joined to `lab.test`. Script built with `--https 0` for the VM's name.

**Steps:**
1. Run the script.
2. Open a session over HTTP 5985 by name with a domain account (Kerberos).
3. Read rule `RemoteHub-WinRM-HTTP-In` and the built-in rules.

**Expected:**
- Session works, `whoami` = the domain account.
- `RemoteHub-WinRM-HTTP-In`: inbound, TCP 5985, only the given addresses. Built-in `WINRM-HTTP-In-TCP*` off.
- No HTTPS listener and no certificate created.
- Note: harness `--via winrm` always uses HTTPS 5986, so an HTTP option is needed. The domain VM is not provided yet.

#### W-TRUST-01 · Trust prompt on the admin's PC
P2 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** 2VM: RemoteHub runs in the client VM, the device is `Server-2025-en-US` with the script from W-HTTPS-01 run. The connection uses WinRM.

**Steps:**
1. In RemoteHub, open the remoting setup for the connection and click "Test again".
2. Compare the thumbprint RemoteHub shows with the one the script printed. Click "Same – trust it".
3. In the Windows security prompt, click Yes (UIA).
4. Let the test run again.

**Expected:**
- CurrentUser\Root of the signed-in user has the certificate with FriendlyName "RemoteHub – WinRM `<host>`" and the printed thumbprint.
- The connection's PowerShell option switches to HTTPS and the test succeeds.
- Declining the Windows prompt instead shows "The Windows prompt was declined – the certificate isn't trusted."

## 9. Agent remote desktop (D)

Shared terms for this section:

- **Harness client**: the headless desktop client in `tests\RemoteHub.LabHarness` (`AgentDesktopClient`: Hello → Start → Status, acks every Video, decodes frames to PNG with RemoteHub's H.264 decoder, sends Mouse/Key/Clipboard/Transfer/Settings/SecureAttention/Ping). Today only `cad` (console session, Start, Settings Fit, optional input, Ctrl+Alt+Del) and `copy` (console session, files to the device with `ClipboardTransfer.Files` + `Chunks`, optional `--offer`/`--ctrlv`) exist. Both always pick the console session and send no `features`. The other commands named below are still to be written.
- **Agent log**: `C:\ProgramData\RemoteHub\Agent\logs\agent-<yyyyMMdd>.log`. **Event**: Application log, source `RemoteHub Agent` (`Get-WinEvent` through PS Direct).
- `<acct>` is the account as Windows writes it, for example `<VM>\PSAdmin`. Session users are written the same way (`<VM>\normaluser`).
- Unless a case says otherwise: `Reset-LabVm`, then `Install-LabAgent` (default policy: Ask+Pin, ConsentTimeoutSeconds 30, max 60 fps / quality 100, clipboard 3, Rights Desktop/SecureDesktop = Administrators). The harness client signs in as PSAdmin.

### 9.1 Connect, Hello, sessions

#### D-CON-01 · Hello
P1 · How: H · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** default policy; Admin at the console.

**Steps:**
1. Harness client opens `/v1/desktop` as PSAdmin.
2. It reads the first message (type 1, Hello) and sends nothing else.
3. It closes the connection.

**Expected:**
- `protocol` 1, `account` `<acct>` (PSAdmin), `device` = device label or computer name.
- `rights` = {control: true, secureDesktop: true, consentBypass: false, autoSignIn: true when the MSI under test carries the credential provider – on by default}; the `autoSignIn` feature with it.
- `consentModes` = ["Ask", "Pin"] (None missing).
- `sessions` not empty; `maxFramesPerSecond` 60, `maxQuality` 100, `consentTimeoutSeconds` 30, `clipboard` 3.
- `features` = allMonitors, stats, ping, resume, transfer, transferReceipt, transferOnPaste; `version` = the installed agent version (same as health).

#### D-CON-02 · Session list
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** Admin at the console; normaluser has a disconnected session (sign normaluser in with Switch user, then switch back to Admin at the console).

**Steps:**
1. PS Direct: `query session` to note the IDs of Admin's and normaluser's sessions.
2. Harness client reads Hello.
3. Compare `sessions` with the `query session` output.

**Expected:**
- First entry has `console: true`: Admin's session, `user` `<VM>\Admin`, `state` "Active".
- normaluser's session is listed with `state` "Disconnected".
- No entry with `id` 0.

#### D-CON-03 · Disconnected session refused
P2 · How: H E · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** as D-CON-02 (normaluser's session disconnected).

**Steps:**
1. Harness client reads Hello and picks normaluser's disconnected session.
2. It sends Start (that session, consent Ask, control).
3. It waits for Status and the close.

**Expected:**
- Status `Failed` "This Windows session can't be shown (it ended or has no screen)."
- Event 2100 "Desktop refused: `<acct>` from `<host IP>` – session `<id>`: This Windows session can't be shown …".
- No helper process started; no event 1100.

#### D-CON-04 · No Start within 5 min
P3 · How: H · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** default.

**Steps:**
1. Harness client reads Hello and sends nothing.
2. It waits up to 5 min 30 s for the close.

**Expected:**
- The agent closes the WebSocket about 5 min after Hello.
- No Status, no helper process, no event 1100/1101.

#### D-CON-05 · First message not Start
P3 · How: H · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** default.

**Steps:**
1. Harness client reads Hello.
2. It sends a Ping (type 28) as first message.
3. It waits for the close.

**Expected:**
- The agent closes the connection without a Status and without a Pong.
- No helper process, no event 1100.

#### D-CON-06 · Desktop off by policy
P1 · How: PS H · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** `Set-LabAgentPolicy DesktopEnabled=0` (DWORD).

**Steps:**
1. `Get-LabAgentHealth`.
2. Probe `/v1/desktop` as PSAdmin (WebSocket upgrade).
3. `Invoke-LabAgentCommand 'hostname'`.

**Expected:**
- Health `features` = ["powershell"] only.
- `/v1/desktop` answers 404.
- PowerShell through the agent still works.

#### D-CON-07 · Rights\Desktop without PSAdmin
P1 · How: PS H · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** `Set-LabAgentPolicy` `Rights\Desktop` = a group PSAdmin is not in (e.g. `S-1-5-32-551`); DesktopViewOnly empty.

**Steps:**
1. Probe `/v1/desktop` as PSAdmin.
2. `Invoke-LabAgentCommand 'whoami'` as PSAdmin.
3. Read the Application log.

**Expected:**
- `/v1/desktop` answers 403.
- PowerShell works.
- Event 2000 "Refused `<user>` from `<host IP>`: …".

#### D-CON-08 · Frames arrive
P1 · How: H C · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** PSAdmin signed in at the console (`Set-LabAutoLogon -Account Admin`), so the own session needs no question.

**Steps:**
1. Harness client: Start (console session, consent None, control, 30 fps, quality 60, monitor 0).
2. Wait for Status `Connected`; save Layout and the first 3 s of Video.
3. Decode the frames to PNG.
4. `Get-LabScreenshot` at the same moment.

**Expected:**
- First Video has the key-frame flag; it decodes.
- Decoded size = Layout `width`×`height` = guest resolution (even numbers).
- Picture ≥ 95 % similar to the console screenshot (banner area excluded, see D-BAN-02).

#### D-CON-09 · End by RemoteHub
P1 · How: H S E · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** connected as in D-CON-08; note the helper PID (`RemoteHubAgent.exe desktop`).

**Steps:**
1. Harness client closes the WebSocket normally.
2. Poll the helper PID every 0.5 s for 5 s.
3. `Invoke-LabInSession` lists top-level windows of class `RemoteHubAgentWindow`.
4. Read the Application log.

**Expected:**
- Event 1101 "Desktop session ended: `<acct>` from `<host IP>` after hh:mm:ss – control, session `<id>` (…), consent None (closed by RemoteHub)".
- Helper process gone within 5 s.
- No banner window left.

#### D-CON-10 · Helper runs as SYSTEM in the session
P2 · How: PS · Needs: – · Status: ✅ Automated – `D-CON.Tests.ps1`

**Preconditions:** connected to normaluser's console session (after Allow, see D-ASK-01).

**Steps:**
1. PS Direct: `Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'"`; pick the one whose command line ends in `desktop`.
2. Read its owner (`GetOwner`) and `SessionId`.
3. Check job membership (`IsProcessInJob` through Add-Type).

**Expected:**
- Command line `"…\RemoteHubAgent.exe" desktop`.
- Owner `NT AUTHORITY\SYSTEM`; `SessionId` = the Start session ID.
- The process is in a job object.

### 9.2 Consent

#### D-ASK-01 · Ask → Allow
P1 · How: H S E · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** normaluser signed in at the console (`Set-LabAutoLogon -Account User`); default policy.

**Steps:**
1. Harness client: Start (normaluser's session, consent Ask, control).
2. Wait for Status `WaitingForUser`.
3. In normaluser's session find the window titled "RemoteHub – Remote access request" (class `RemoteHubAgentWindow`); OCR its text from `Get-LabScreenshot`.
4. Click Allow (second button from the right in the footer, about 176 px left of the right edge and 32 px above the bottom at 100 % scaling).
5. Wait for Status `Connected`; read the Application log.

**Expected:**
- Status `WaitingForUser` "Waiting for `<VM>\normaluser` to allow access…".
- Dialog heading "`<acct>` wants to see and control your screen", footnote "Denied automatically in 30 s" counting down.
- After Allow: Status `Connected` "Connected." with a `resume` token; dialog closed, banner shown.
- Event 1100 "Desktop session started: `<acct>` from `<host IP>` – control, session `<id>` (`<VM>\normaluser`), consent Ask".

Note: the consent, PIN and banner windows are custom-drawn Win32 popups of the SYSTEM helper. UI Automation sees only the window (title, bounds), not the text, the PIN or the buttons, and UIPI blocks clicks sent from a task running as normaluser. Text and PIN need OCR on the console screenshot; buttons need a click at their position via the console mouse (Hyper-V `Msvm_SyntheticMouse`, not in the lab spec yet); Esc works through the console keyboard. The heading shows the full account (`<VM>\PSAdmin`), not just "PSAdmin".

#### D-ASK-02 · Ask → Deny
P1 · How: H S E · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** as D-ASK-01.

**Steps:**
1. Harness client: Start (normaluser's session, consent Ask).
2. Wait for `WaitingForUser` and the dialog.
3. Click Deny (rightmost footer button, about 72 px left of the right edge).
4. Read Status and the Application log.

**Expected:**
- Status `Denied` "The user at the device didn't allow it."
- Event 2100 "Desktop refused: `<acct>` from `<host IP>` – control, session `<id>` (`<VM>\normaluser`), consent Ask: The user at the device didn't allow it."
- No event 1100; helper gone, no banner.

#### D-ASK-03 · Ask → no answer
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** as D-ASK-01.

**Steps:**
1. Harness client: Start (consent Ask); note the time of `WaitingForUser`.
2. Nobody answers; take a screenshot at +5 s and +25 s.
3. Wait for Status `Denied` (at most 40 s).

**Expected:**
- Footnote counts down ("Denied automatically in 30 s" … "in 5 s").
- Status `Denied` about 30 s after `WaitingForUser`, in any case before +40 s.
- Dialog closed; event 2100.

Note: the dialog's own countdown answers "deny", so the Status text is "The user at the device didn't allow it." "No answer in time." comes only from the service's fallback at ConsentTimeoutSeconds + 10 s, when the helper sends no answer at all.

#### D-ASK-04 · Esc / close = deny, Enter never allows
P1 · How: H S C · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** as D-ASK-01.

**Steps:**
1. Harness client: Start (consent Ask); wait for the dialog.
2. `Send-LabKey Enter` on the console.
3. Wait 3 s; check Status and that the dialog is still there.
4. `Send-LabKey Esc`.
5. Read Status.

**Expected:**
- After Enter: still `WaitingForUser`, dialog still open.
- After Esc: Status `Denied` "The user at the device didn't allow it."; event 2100.

#### D-ASK-05 · Timeout policy
P2 · How: PS H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** normaluser at the console.

**Steps:**
1. `Set-LabAgentPolicy ConsentTimeoutSeconds=5`; harness client reads Hello, Start (Ask); screenshot at once.
2. Measure the time from `WaitingForUser` to `Denied`.
3. Repeat with `ConsentTimeoutSeconds=4`, then `601`; read Hello each time.

**Expected:**
- Value 5: Hello `consentTimeoutSeconds` 5; footnote "Denied automatically in 5 s"; `Denied` after about 5 s.
- Values 4 and 601: Hello shows 30; footnote starts at 30 s.

#### D-ASK-06 · No input before consent
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** normaluser at the console; marker app (counts mouse and key input) running in normaluser's session.

**Steps:**
1. Harness client: Start (consent Ask); wait for `WaitingForUser`.
2. Send 20 Mouse moves/clicks over the marker app and 10 Key down/up pairs, plus SecureAttention and a Clipboard text.
3. Deny the dialog with Esc (console).
4. Read the marker app's counters and the clipboard in the session.

**Expected:**
- Marker app counted no input; the session's clipboard is unchanged.
- No Ctrl+Alt+Del screen appeared.

#### D-ASK-07 · View-only text
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** normaluser at the console.

**Steps:**
1. Harness client: Start (consent Ask, `viewOnly: true`).
2. OCR the dialog.
3. Esc to deny.

**Expected:**
- Heading "`<acct>` wants to see your screen" ("control" does not appear).

#### D-PIN-01 · PIN right
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** normaluser at the console.

**Steps:**
1. Harness client: Start (consent Pin).
2. Wait for Status `EnterPin`.
3. OCR the 6 digits from the dialog on the console screenshot.
4. Send Pin {"pin": "<digits>"}.

**Expected:**
- Status `EnterPin` "Ask `<VM>\normaluser` for the code shown on their screen." with `pinAttemptsLeft` 3.
- Dialog body "If you agree, read this code out to them. Nobody can connect without it." and 6 digit boxes.
- After the PIN: Status `Connected` "Connected."; event 1100 with "consent Pin".

Note: the lab spec says the PIN is read by AutomationId; the code has no UIA element for it (painted text), so only OCR works. OCR sometimes returns one of the large digits as a look-alike letter (0 as "O" or "o", 1 as "I", "l" or "|"); `RhPerson.ps1 -Do pin` counts such one-letter words of digit height as digits.

#### D-PIN-02 · PIN wrong ×3
P1 · How: H S E · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** as D-PIN-01.

**Steps:**
1. Harness client: Start (consent Pin); read the real PIN by OCR.
2. Send three different wrong 6-digit PINs, waiting for Status after each.
3. Read the Application log.

**Expected:**
- After 1st and 2nd: Status `EnterPin` "That code isn't right. Try again." with `pinAttemptsLeft` 2, then 1.
- After 3rd: Status `Denied` "The code was entered wrong too often."
- Event 2100 ending in "consent Pin: The code was entered wrong too often."

#### D-PIN-03 · PIN with spaces / dash
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** as D-PIN-01.

**Steps:**
1. Harness client: Start (consent Pin); read PIN `abcdef` by OCR.
2. Send Pin "abc def" (with a space).
3. In a second run send "abc-def".

**Expected:**
- Both runs: Status `Connected` (non-digits are stripped before the compare).

#### D-PIN-04 · PIN dialog Deny
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** as D-PIN-01.

**Steps:**
1. Harness client: Start (consent Pin); wait for `EnterPin`.
2. Click Deny in the PIN dialog (its only button); in a second run press Esc on the console.

**Expected:**
- Status `Denied` "The user at the device refused."; event 2100.

#### D-PIN-05 · PIN timeout
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** as D-PIN-01.

**Steps:**
1. Harness client: Start (consent Pin); note the time of `EnterPin`.
2. Send nothing; wait up to 3 min 20 s.

**Expected:**
- Status `Denied` "No answer in time." about 3 min after `EnterPin`.
- PIN dialog gone; event 2100.

#### D-NONE-01 · None refused by default
P1 · How: H E · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** default policy; normaluser at the console.

**Steps:**
1. Harness client reads Hello.
2. Start (normaluser's session, consent None).
3. Read Status and the Application log.

**Expected:**
- Hello `consentModes` has no "None".
- Status `Failed` "Connecting with "None" isn't allowed on this device."
- Event 2100; no dialog, no helper.

#### D-NONE-02 · None with policy only
P1 · How: PS H · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** `AllowedConsentModes` = Ask, Pin, None (REG_MULTI_SZ); `Rights\ConsentBypass` empty; normaluser at the console.

**Steps:**
1. Harness client reads Hello.
2. Start (consent None).

**Expected:**
- Hello `consentModes` = ["Ask", "Pin"]; `rights.consentBypass` false.
- Status `Failed` "Connecting with "None" isn't allowed on this device."; event 2100.

#### D-NONE-03 · None with policy + right
P1 · How: PS H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** `AllowedConsentModes` incl. None; `Rights\ConsentBypass` = `S-1-5-32-544`; normaluser at the console.

**Steps:**
1. Harness client reads Hello.
2. Start (consent None).
3. Screenshot of the console; look for the dialog and the banner.

**Expected:**
- Hello lists "None"; `rights.consentBypass` true.
- Status `Connected` without `WaitingForUser`; no dialog.
- Banner "Your screen is being viewed and controlled by `<acct>`" on the screenshot.
- Event 1100 with "consent None".

#### D-NONE-04 · Own session
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** PSAdmin signed in at the console (`Set-LabAutoLogon -Account Admin`); default policy (None not allowed).

**Steps:**
1. Harness client (PSAdmin): Start (console session, consent None).
2. Screenshot; read the Application log.

**Expected:**
- Status `Connected`, no dialog; banner shown.
- Event 1100 "… session `<id>` (`<VM>\PSAdmin`), consent None".

Note: the sheet expects event 1102 "own account". For a Start on the own session the code writes 1100 with "consent None"; 1102 "… signed in … with their own account – continued without asking" is written only when the admin signs in with the own account during a running session (D-SIGNIN-02, D-SAS-07).

#### D-MODES-01 · Policy as REG_SZ list / invalid list
P3 · How: PS H · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** normaluser at the console.

**Steps:**
1. Set `AllowedConsentModes` as REG_SZ "Pin;Ask"; harness client reads Hello; Start (Pin), then deny with Esc.
2. Set REG_SZ "Foo"; read Hello.

**Expected:**
- "Pin;Ask": Hello `consentModes` = ["Pin", "Ask"]; Start Pin gives `EnterPin`.
- "Foo": Hello `consentModes` = ["Ask", "Pin"] (defaults).

#### D-MODES-02 · Only Pin allowed
P2 · How: PS H · Needs: – · Status: ✅ Automated – `D-CONSENT.Tests.ps1`

**Preconditions:** `AllowedConsentModes` = Pin; normaluser at the console.

**Steps:**
1. Harness client: Start (consent Ask).
2. New connection: Start (consent Pin); enter the OCR'd PIN.

**Expected:**
- Ask: Status `Failed` "Connecting with "Ask" isn't allowed on this device."; event 2100.
- Pin: `EnterPin`, then `Connected`.

### 9.3 Banner and user at the device

#### D-BAN-01 · Banner shown
P1 · How: S C · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** connected to normaluser's session after Allow (D-ASK-01).

**Steps:**
1. Within 5 s of `Connected`, `Invoke-LabInSession` finds the window titled "RemoteHub" of class `RemoteHubAgentWindow` and reads its bounds and extended style.
2. `Get-LabScreenshot`; OCR the top centre.

**Expected:**
- Window is topmost, at the top centre (8 px from the top at 100 %).
- Screenshot shows "Your screen is being viewed and controlled by `<acct>`" and a "Disconnect" button.

Note: UIA does not expose the text or the Disconnect button (see D-ASK-01 note); the checks are window bounds/style plus OCR.

#### D-BAN-02 · Banner not in the picture
P2 · How: H C · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** as D-BAN-01; Windows build ≥ 19041.

**Steps:**
1. Harness client saves a decoded frame while the banner is expanded.
2. `Get-LabScreenshot` at the same time.
3. Compare the banner area (top centre) of both.

**Expected:**
- Screenshot shows the banner; the decoded frame shows the desktop behind it (no banner, not black).

#### D-BAN-03 · Banner in the picture on 1809
P2 · How: H · Needs: 1809 · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** W10-1809 VM (build 17763); connected after Allow.

**Steps:**
1. Harness client saves a decoded frame.
2. OCR / template-match the top centre of the frame.

**Expected:**
- The banner is visible in the frame, not a black rectangle.

#### D-BAN-04 · Collapse to pill
P3 · How: S · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** as D-BAN-01; mouse not over the banner.

**Steps:**
1. Read the banner bounds at +2 s and at +10 s after `Connected`.
2. Move the console mouse over the banner; read the bounds.
3. Move the mouse away; read the bounds after 4 s.

**Expected:**
- +2 s: full banner (height 46 px at 100 %).
- +10 s: pill (height 36 px, narrower, text "Controlled by `<acct>`").
- Hover: full banner again; about 3 s after leaving: pill again.

#### D-BAN-05 · Disconnect by the user
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** as D-BAN-01; harness keeps the `resume` token.

**Steps:**
1. Click "Disconnect" on the banner with the console mouse: `Invoke-LabBannerClick` – hover first (by now the banner is the pill, which has no Disconnect), then the whole label found in the console picture (the banner is kept out of the session's captures); the picture is kept as `d-banner-control\disconnect-console.png`.
2. Read Status and the Application log.
3. Harness client opens a new connection: Start with `resume` = the token.

**Expected:**
- Status `Ended` "The user at the device ended the session."
- Event 1101 ending in "(The user at the device ended the session.)".
- The resume Start gets `WaitingForUser` again (token void).

#### D-BAN-06 · Can't be closed otherwise
P2 · How: S · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** as D-BAN-01.

**Steps:**
1. `Invoke-LabInSession` (normaluser): post WM_CLOSE to the banner window.
2. `Invoke-LabInSession`: `taskkill /F /IM RemoteHubAgent.exe`.
3. Start the marker window in normaluser's session and click it on the console, so it is the foreground window; click
   the banner's text and press Alt+F4 on the console. The banner never takes the focus (WS_EX_NOACTIVATE), so the key
   goes to the foreground window – without the marker that is the shell, and on Server 2025 normaluser was signed out.
4. Check the banner window and the Status stream.

**Expected:**
- Banner still there; taskkill reports access denied.
- Session still `Connected` (no `Ended`).

#### D-BAN-07 · View-only text
P3 · How: S · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** connected view-only to normaluser's session after Allow.

**Steps:**
1. OCR the banner right after `Connected` and after it collapsed.

**Expected:**
- "Your screen is being viewed by `<acct>`" (no "controlled"); pill "Viewed by `<acct>`".

### 9.4 View-only and rights

#### D-VIEW-01 · View-only by choice
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** normaluser at the console; marker app running; note the guest resolution.

**Steps:**
1. Harness client: Start (`viewOnly: true`, consent Ask); Allow.
2. Send mouse moves/clicks, keys, SecureAttention, Clipboard text and a TransferStart (files).
3. Send Settings with Fit 1600×900.
4. Read marker counters, the session's clipboard and the resolution.

**Expected:**
- Status `Connected` "Connected (view only)."; Layout `viewOnly` true.
- Marker app saw nothing; no Ctrl+Alt+Del screen; clipboard unchanged.
- TransferEnd `ok: false` "Copying files isn't allowed on this device."
- Resolution unchanged; Stats `fit` empty.

#### D-VIEW-02 · View-only by right
P1 · How: PS H S · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** local account `viewer` (lab password, standard user) created by the test; `Rights\DesktopViewOnly` = viewer; normaluser at the console.

**Steps:**
1. Harness client signs in as viewer; reads Hello.
2. Start (`viewOnly: false`, consent Ask); OCR the dialog; Allow.
3. Send Key and Mouse to the marker app.

**Expected:**
- Hello `rights` = {control: false, secureDesktop: false, consentBypass: false}, `clipboard` 0.
- Dialog "`<VM>\viewer` wants to see your screen"; Status `Connected` "Connected (view only)."
- Marker app saw no input; event 1100 "view-only, …".

#### D-VIEW-03 · Key release still passes
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** `Rights\SecureDesktop` = a group PSAdmin is not in; marker app running in normaluser's session; connected with control to that session after Allow.

**Steps:**
1. Harness client clicks on the marker (it gets the keyboard focus back – the consent dialog had it), then sends Key down A (scan 0x1E).
2. A task in normaluser's session runs `Start-Process cmd -Verb RunAs` (UAC on the secure desktop).
3. Harness client sends Key up A while the prompt is open.
4. Close the prompt; check the A key's state in the session.

**Expected:**
- The marker recorded the click and the key-down (precondition: the key really was held in the session).
- A is not stuck after the prompt (`GetAsyncKeyState` in the session shows it up).

Note: view-only can't be switched on during a session (it is fixed at Start, and the service drops all keys in view-only). The code only lets the release of a held key through when input becomes not allowed on the current desktop (secure desktop without the right, or a held user desktop); the case is written for that.

#### D-RIGHT-01 · No SecureDesktop right on the lock screen
P1 · How: PS H E · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** `Rights\SecureDesktop` = a group PSAdmin is not in; normaluser at the console, locked (`Lock-LabSession`).

**Steps:**
1. Harness client reads Hello; Start (normaluser's session, consent Ask).
2. Read Status and the Application log.

**Expected:**
- Hello `rights.secureDesktop` false; the session's `state` "Locked".
- Status `Failed` "Nobody is signed in or the session is locked – and your account may not see the sign-in screen on this device."
- Event 2100.

#### D-RIGHT-02 · No SecureDesktop right during UAC
P1 · How: PS H S · Needs: – · Status: ✅ Automated – `D-BANNER.Tests.ps1`

**Preconditions:** as D-VIEW-03 (no SecureDesktop right, connected with control after Allow).

**Steps:**
1. Count Video frames for 3 s.
2. Task in normaluser's session: `Start-Process cmd -Verb RunAs`; wait for `consent.exe`.
3. Count frames for 5 s; harness client sends Enter (scan 0x1C) and a mouse click.
4. Check that `consent.exe` still runs; press Esc on the console.

**Expected:**
- No Video while the prompt is up (a Layout with `secureDesktop: true` may arrive).
- Prompt still open after the harness's Enter.
- Agent log: "Desktop: session `<id>`: no picture for 3 s (the secure desktop is up and this connection may not see it) …".

### 9.5 Secure desktop, sign-in, lock, user switch

#### D-LOCK-01 · Connect to a locked session
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** normaluser at the console, locked; default rights.

**Steps:**
1. Harness client: Start (normaluser's session, consent Ask).
2. Watch for a dialog (screenshot) and Status.
3. Save decoded frames for 5 s.

**Expected:**
- No `WaitingForUser`; Status `Connected`.
- Event 1100 "… session `<id>` (`<VM>\normaluser`), consent None (on unlock or sign-in: Ask)".
- Frames show the lock screen; no dialog on the device.

Note: on Windows 11 / Server 2025 the "Press Ctrl+Alt+Delete" lock screen runs on the Default desktop; the helper then sends no frames until Ctrl+Alt+Del brings up the sign-in screen (D-LOCK-05). On those builds "lock screen in the picture" holds only after Ctrl+Alt+Del.

#### D-LOCK-02 · User unlocks while connected
P1 · How: H S C E · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** connected to normaluser's locked session as in D-LOCK-01.

**Steps:**
1. Console: `Send-LabCtrlAltDel`, type normaluser's password, Enter.
2. Harness client counts Video frames after the unlock.
3. Wait for `WaitingForUser`; click Allow.
4. Read Status and the Application log.

**Expected:**
- No frame of normaluser's desktop before Allow.
- Status `WaitingForUser` "Waiting for `<VM>\normaluser` to allow access…".
- After Allow: Status `Connected` "`<VM>\normaluser` allowed access."; frames show the desktop.
- Event 1102 "Desktop: `<VM>\normaluser` in session `<id>` allowed the remote desktop of `<acct>` from `<host IP>` (Ask)".

#### D-LOCK-03 · User unlocks and refuses
P1 · How: H S C E · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** as D-LOCK-02.

**Steps:**
1. Console: unlock as normaluser.
2. Wait for the dialog; click Deny.
3. Read Status, the close and the Application log.

**Expected:**
- Status `Denied` "The user at the device didn't allow it."; connection closed.
- Event 2101 "Desktop: `<VM>\normaluser` in session `<id>` didn't allow the remote desktop of `<acct>` from `<host IP>` – ended (…)".
- Event 1101 with "(`<VM>\normaluser` didn't allow it (…))".

#### D-LOCK-04 · Lock while connected (Win+L by the user)
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** connected to normaluser's session after Allow.

**Steps:**
1. `Lock-LabSession` (task in normaluser's session: `rundll32 user32.dll,LockWorkStation`).
2. Save frames for 10 s; watch Status.

**Expected:**
- No `Ended`/`Failed`; connection stays.
- After Allow the layout passes through the secure desktop (`secureDesktop` true) and the last picture is the lock screen, no longer normaluser's desktop (the marker: under 5 % magenta). On Server 2025 / Windows 11 the lock screen itself runs on the Default desktop, so the last layout is not secure (run 20261019-193627).

#### D-LOCK-05 · Win11 lock screen on the Default desktop
P1 · How: H C · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** Server 2025 / Windows 11 VM; connected to normaluser's locked session (D-LOCK-01).

**Steps:**
1. Harness client counts frames for 10 s.
2. Read the agent log.
3. Console: `Send-LabCtrlAltDel`; count frames again.

**Expected:**
- No frames while the "Press Ctrl+Alt+Delete" screen shows; nothing of the user's desktop.
- Agent log: "no picture for 3 s (the lock screen is up – waiting for Ctrl+Alt+Del)".
- After Ctrl+Alt+Del: frames show the sign-in screen (Layout `secureDesktop: true`).

#### D-SIGNIN-01 · Nobody signed in
P1 · How: H C · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** console signed out (`logoff` of Admin's session via PS Direct); `Get-LabScreenshot` shows the sign-in screen.

**Steps:**
1. Harness client reads Hello; Start (console session, consent Ask).
2. Save frames for 5 s (after Ctrl+Alt+Del if the screen waits for it).

**Expected:**
- Console session `state` "SignIn", `user` "".
- Status `Connected` without a question; event 1100 "… (sign-in screen), consent None (on unlock or sign-in: Ask)".
- Frames show the sign-in screen.

#### D-SIGNIN-02 · Admin signs in through the picture
P1 · How: H · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** nobody signed in (D-SIGNIN-01); harness client signs in to the agent as Admin.

**Steps:**
1. Harness client (Admin): Start (console session, consent Ask).
2. Send SecureAttention; wait for the credential field in the frames.
3. Type Admin's password as Key messages (scan codes), Enter.
4. Wait for the desktop in the frames; read Status and the Application log.

**Expected:**
- No `WaitingForUser`; picture continues on Admin's desktop.
- Event 1102 "Desktop: `<VM>\Admin` from `<host IP>` signed in to session `<id>` with their own account – continued without asking".

Note: "no consent" applies only when the account that signs in is the one connected to the agent. If the harness connects as PSAdmin and types Admin's password, Admin is asked.

#### D-SIGNIN-03 · Someone else signs in at the console
P1 · How: H C S · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** nobody signed in; harness client connected as PSAdmin (D-SIGNIN-01).

**Steps:**
1. Console keyboard signs normaluser in.
2. Watch Status; wait for `WaitingForUser`.
3. Click Allow in normaluser's session.
4. Read Status and the Application log.

**Expected:**
- Possibly Status `Connected` "The device's screen changed (sign-in or another session) – reconnecting…".
- `WaitingForUser` "Waiting for `<VM>\normaluser` to allow access…"; no frame of the desktop before Allow.
- After Allow: `Connected` "`<VM>\normaluser` allowed access."; event 1102.

#### D-SIGNIN-04 · Sign out while connected
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** connected to normaluser's console session after Allow.

**Steps:**
1. Task in normaluser's session: `logoff`.
2. Watch Status; save frames for 15 s.

**Expected:**
- Status `Connected` "The device's screen changed (sign-in or another session) – reconnecting…".
- Frames then show the sign-in screen; connection stays.

#### D-UAC-01 · UAC with SecureDesktop
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** default rights; connected with control to normaluser's session after Allow.

**Steps:**
1. Task in normaluser's session: `Start-Process cmd -Verb RunAs`.
2. Wait for a Layout with `secureDesktop: true`; save frames; read Stats.
3. Through the desktop session type the password of the administrator the prompt offers, Enter. For a standard user Windows lists the device's administrators with the first one (Admin, the console account) chosen and its password field focused – a typed user name would go into the password field.
4. PS Direct: check for an elevated `cmd.exe` in normaluser's session; close a prompt that is still open (`consent.exe`), so the next cases don't start on the secure desktop.

**Expected:**
- Frames show the UAC credential prompt with Admin chosen; Stats `capture` "GDI".
- Agent log: "Desktop: session `<id>` switched to the secure desktop (sign-in, lock or UAC screen)".
- Elevated cmd.exe runs (as Admin) in that session.

#### D-UAC-02 · UAC prompt not leaked to view-only users
P – · How: – · Needs: – · Status: see D-RIGHT-02

Covered by D-RIGHT-02 (no separate steps).

#### D-FUS-01 · Fast user switching
P2 · How: H S C · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** connected to normaluser's console session after Allow.

**Steps:**
1. Task in normaluser's session: `tsdiscon` (console goes to the sign-in screen).
2. Console keyboard signs Admin in.
3. Wait for `WaitingForUser`; screenshot of the dialog in Admin's session.
4. Click Allow.

**Expected:**
- Agent log: "Desktop: the console moved from session `<a>` to `<b>` – following it".
- Status `WaitingForUser` "Waiting for `<VM>\Admin` to allow access…".
- After Allow: `Connected` "`<VM>\Admin` allowed access."; event 1102.

#### D-FUS-02 · Different user = new consent after helper restart
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** as D-FUS-01.

**Steps:**
1. Switch to Admin as in D-FUS-01.
2. Admin's dialog: click Deny.
3. Read Status and the Application log.

**Expected:**
- Admin is asked although normaluser allowed earlier.
- Status `Denied` "The user at the device didn't allow it."; event 2101 for `<VM>\Admin`.

#### D-DISC-01 · Session disconnected while connected
P2 · How: H PS · Needs: 2VM (or host mstsc) · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** normaluser has an RDP session (from a second VM or host mstsc); harness client connected to it after Allow.

**Steps:**
1. PS Direct: `tsdiscon <id>`.
2. Wait up to 5 s for Status.
3. Reconnect the RDP session.
4. Wait up to 5 s for Status.

**Expected:**
- Status `Paused` "This Windows session is disconnected (its remote desktop connection was closed), so it has no screen right now. The picture continues when someone connects to the session again."
- After the reconnect: `Connected` "The session has a screen again."

Note: for the console session `tsdiscon` moves the console to a new session; the service then follows the console (sign-in screen) right after the Paused status. The Paused/"has a screen again" pair is only stable for an RDP session.

#### D-RDPSES-01 · Connect to a user's RDP session
P2 · How: H · Needs: 2VM (or host mstsc) · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** normaluser signed in by RDP from the second VM (client name known).

**Steps:**
1. Harness client reads Hello.
2. Start (the RDP session, consent Ask); Allow in that session.
3. Save frames.

**Expected:**
- Hello lists the session with `console: false`, `state` "Active", `client` = the RDP client's computer name, `station` "RDP-Tcp#…".
- `Connected`; frames show the RDP session's desktop.

### 9.6 Ctrl+Alt+Del

#### D-SAS-01 · Console session
P1 · How: H E · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** PSAdmin at the console (own session) or normaluser after Allow. Harness `cad` covers the flow (`--consent None` on the own session).

**Steps:**
1. Harness client connects to the console session and waits for `Connected`.
2. Send SecureAttention.
3. Save frames for 6 s; read the agent log.

**Expected:**
- Frames show the security screen (LogonUI; Layout `secureDesktop: true`).
- Agent log: "Desktop: Ctrl+Alt+Del for `<acct>` in session `<id>` by the service: sent (SoftwareSASGeneration = 1, console session `<id>`, last error …)".

#### D-SAS-02 · RDP session
P2 · How: H · Needs: 2VM (or host mstsc) · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** connected to normaluser's RDP session (D-RDPSES-01); Admin at the console.

**Steps:**
1. Send SecureAttention.
2. Save frames; `Get-LabScreenshot` of the console.
3. Read the agent log.

**Expected:**
- Frames of the RDP session show the security screen; the console screenshot does not change.
- Agent log: "… in session `<rdp id>` by the helper: sent …".

#### D-SAS-03 · Without SecureDesktop right
P2 · How: PS H · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** `Rights\SecureDesktop` = a group PSAdmin is not in; connected with control to normaluser's session after Allow.

**Steps:**
1. Send SecureAttention.
2. Read Status; save frames.

**Expected:**
- Status `Connected` "Your account may not use the sign-in screen on this device."
- No security screen.

#### D-SAS-04 · Policy 0
P2 · How: PS H · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\SoftwareSASGeneration` = 0 and `C:\ProgramData\RemoteHub\Agent\sas.agent` deleted; connected to the console session.

**Steps:**
1. Send SecureAttention.
2. Read Status; save frames; read the registry value again.

**Expected:**
- Status `Connected` "Ctrl+Alt+Del from software is turned off by a policy on this device."
- No security screen; the value is still 0.

Note: the agent writes the `sas.agent` marker at start and then treats the value as its own; with the marker present `Ensure()` sets the value back to 1 on the next Ctrl+Alt+Del. The test must delete the marker (or set the value before installing).

#### D-SAS-05 · Policy 3 (services + ease of access)
P3 · How: PS H · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** `SoftwareSASGeneration` = 3 and `sas.agent` deleted (see D-SAS-04); connected to the console session.

**Steps:**
1. Send SecureAttention.
2. Save frames; read the registry value.

**Expected:**
- Security screen in the frames; no refusal Status; the value is still 3.

#### D-SAS-06 · On the lock screen
P1 · How: H S C · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** normaluser at the console, locked; harness client connected (D-LOCK-01).

**Steps:**
1. Send SecureAttention; wait for the password field in the frames.
2. Type normaluser's password as Key messages, Enter.
3. Wait for `WaitingForUser`; click Allow in the session.

**Expected:**
- Frames show the password field after Ctrl+Alt+Del.
- After the password: session unlocked; no desktop frame before consent.
- Then as D-LOCK-02 (`WaitingForUser`, Allow → `Connected`, event 1102).

#### D-SAS-07 · On the sign-in screen (nobody signed in)
P1 · How: H C · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** nobody signed in; harness client (PSAdmin) connected to the console (D-SIGNIN-01).

**Steps:**
1. Send SecureAttention; wait for the credential field.
2. Type PSAdmin's password (switch user to PSAdmin first if another name is preset), Enter.
3. Wait for the desktop frames; read the Application log.

**Expected:**
- No `WaitingForUser`; frames show PSAdmin's desktop.
- Event 1102 "… signed in to session `<id>` with their own account – continued without asking".

#### D-SAS-08 · On the desktop
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** connected with control to normaluser's session after Allow.

**Steps:**
1. Send SecureAttention; save frames.
2. OCR the frame for Lock, Switch user, Sign out, Task Manager.
3. Send Esc (scan 0x01); save frames.

**Expected:**
- Security options screen in the picture (Layout `secureDesktop: true`).
- After Esc: desktop again (Layout `secureDesktop: false`); connection stays, no new question.

#### D-SAS-09 · Lock through Ctrl+Alt+Del
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** as D-SAS-08.

**Steps:**
1. Send SecureAttention.
2. Click "Lock" in the picture (Mouse down/up at the OCR'd position).
3. Check the session state (LogonUI in that session) and Status.

**Expected:**
- Session locked; connection stays; frames show the lock screen.

#### D-SAS-10 · Task Manager through Ctrl+Alt+Del
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** as D-SAS-08.

**Steps:**
1. Send SecureAttention.
2. Click "Task Manager" in the picture.
3. PS Direct: `Get-Process Taskmgr` with its `SessionId`.

**Expected:**
- Taskmgr.exe runs in normaluser's session; frames show it.

#### D-SAS-11 · Repeated presses
P3 · How: H E · Needs: – · Status: ✅ Automated – `D-SECURE.Tests.ps1`

**Preconditions:** as D-SAS-08 (or own console session).

**Steps:**
1. 10 times: SecureAttention, wait 3 s, Esc, wait 3 s.
2. Read the agent log.

**Expected:**
- Every press shows the security screen in the frames.
- 10 log lines "Desktop: Ctrl+Alt+Del for `<acct>` … : sent …".

### 9.7 Input

#### D-IN-01 · Keyboard by scan code
P1 · How: H S · Needs: de-DE (second run) · Status: ✅ Automated – `D-INPUT.Tests.ps1`

**Preconditions:** connected with control after Allow; marker app (text box, logs characters) in normaluser's session, clicked through the connection first so it has the keyboard focus (the consent dialog takes the foreground and Windows needn't give it back when it closes – also for D-IN-02, D-IN-05 and D-IN-06); en-US VM, then de-DE VM. What counts is the session's keyboard layout, not the Windows language: an en-US VM can have a German keyboard first.

**Steps:**
1. Send Key down/up for scan codes of `z` (0x2C), `y` (0x15), the `;`/`ö` key (0x27), the `'`/`ä` key (0x28).
2. Send AltGr (0x38 extended) + `q` (0x10).
3. Read the marker app's text.

**Expected:**
- en-US: "zy;'" and no "@" from AltGr+q.
- de-DE: "yzöä@" (the device's layout applies).
- Automated: the text `The quick brown fox 0123456789 !@#$%^&*()_+-=[]{};:,./<>?` sent by US scan codes arrives unchanged on a US layout and as `… !"§$%&/()=?` followed by the dead-key and umlaut characters on a German one; another layout is skipped.

#### D-IN-02 · Extended keys
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-INPUT.Tests.ps1`

**Preconditions:** as D-IN-01; marker app logs virtual-key codes.

**Steps:**
1. Send with the Extended flag: arrows (0x48/0x50/0x4B/0x4D), Home 0x47, End 0x4F, Del 0x53, right Ctrl 0x1D, numpad Enter 0x1C.
2. Read the marker log.

**Expected:**
- VK_UP/DOWN/LEFT/RIGHT, VK_HOME, VK_END, VK_DELETE, VK_RCONTROL and Enter with the extended bit, in order, each down and up.

#### D-IN-03 · Mouse
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-INPUT.Tests.ps1`

**Preconditions:** as D-IN-01; marker app full screen, logs screen coordinates and buttons.

**Steps:**
1. Send Move to 5 known points (pixels of the shown monitor).
2. Send Down/Up for Left, Right, Middle, X1, X2 at a point.
3. Send Wheel ±120 and HorizontalWheel ±120, a second apart (Windows merges wheel turns still queued for the window into one with the sum: 120 and −240 sent together arrive as one −120).

**Expected:**
- Each point arrives within ±2 px.
- All 5 buttons down and up in order; wheel and horizontal wheel with the sent delta.

#### D-IN-04 · Win key combos
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-INPUT.Tests.ps1`

**Preconditions:** connected with control after Allow; two windows open in the session.

**Steps:**
1. Send Win (0x5B extended) down, R down/up, Win up; check for the Run dialog.
2. Esc; send Alt down, Tab down/up, Alt up; check the foreground window.

**Expected:**
- Run dialog opens; Alt+Tab switches the foreground window.

#### D-IN-05 · No stuck keys
P2 · How: H S N · Needs: – · Status: ✅ Automated – `D-INPUT.Tests.ps1`

**Preconditions:** as D-IN-01.

**Steps:**
1. Send Key down Shift and Mouse Down Left.
2. Cut the VM's network adapter.
3. Wait until the helper process ends (up to 70 s; WebSocket keep-alive 30 s + 30 s).
4. Restore the network; check Shift and left button state in the session.

**Expected:**
- Shift and the left button are up after the helper ended.

#### D-IN-06 · Input flood
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-INPUT.Tests.ps1`

**Preconditions:** as D-IN-01.

**Steps:**
1. Send 10,000 Mouse moves as fast as possible, with 50 Key down/up pairs mixed in.
2. Wait 5 s; read the marker counts and agent health.

**Expected:**
- No crash; connection stays; agent healthy.
- All 50 key pairs arrive in order (mouse moves may be dropped when the helper's queue of 256 is full).

#### D-IN-07 · DPI 150 %
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-INPUT.Tests.ps1`

**Preconditions:** normaluser's display scale set to 150 % (sign out/in); connected after Allow.

**Steps:**
1. Repeat D-IN-03 step 1.

**Expected:**
- Points arrive within ±2 px of the sent physical pixels.

### 9.8 Picture, encoder, resolution, monitors

#### D-VID-01 · Presets
P2 · How: H · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** connected (own session); a moving marker window keeps the screen changing.

**Steps:**
1. Start with 15 fps; read 3 Stats messages (every 2 s).
2. Settings 30 fps, then 60 fps; read Stats after each.
3. Start once without choosing a quality preset (RemoteHub's default 60).

**Expected:**
- Stats `framesPerSecond` ≤ the requested value each time.
- Stats `quality` 60 when quality 60 is used.

#### D-VID-02 · Policy limits
P2 · How: PS H · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** `MaxFramesPerSecond=10`, `MaxQuality=40`; moving marker window.

**Steps:**
1. Harness client reads Hello; Start with 60 fps, quality 100.
2. Read Stats for 6 s.
3. Settings 60 fps, quality 90; read Stats.

**Expected:**
- Hello `maxFramesPerSecond` 10, `maxQuality` 40.
- Stats `framesPerSecond` ≤ 10 and `quality` ≤ 40, before and after Settings.

#### D-VID-03 · Changes only
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** connected; still desktop.

**Steps:**
1. Count Video frames for 10 s with nothing moving.
2. Start a window in the console session that changes colour on every tick of a 15 ms timer and records how often it changed; connect at 30 fps and read Stats for 12 s.

**Expected:**
- Still screen: after the first frame at most 3 more (refinement frames), then none.
- Changing window (more than 10 changes a second): frames keep arriving, Stats above 5 fps.

Note: moving the pointer alone changes no frame – the pointer goes as its own Cursor message – so mouse moves are no stimulus for frame counts.

#### D-VID-04 · Key frame request
P3 · How: H · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** connected; moving marker window.

**Steps:**
1. Wait 5 s (non-key frames arrive).
2. Send KeyFrameRequest (type 27).
3. Check the next Video's flag.

**Expected:**
- The next frame has the key-frame flag.

#### D-VID-05 · Slow network
P2 · How: H N · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** connected at 30 fps, quality 80; a 480×360 window in the console session showing new random pixels on every 15 ms tick (far more than the link carries); VM network limited to 2 Mbit/s (`Set-VMNetworkAdapter -MaximumBandwidth`).

**Steps:**
1. Read Stats for 30 s.
2. Second run: harness client stops sending Acks for 5 s; count Video meanwhile.
3. Send a Mouse move through the session; check it lands.

**Expected:**
- Stats `quality` goes down in steps of 10 (not below 20).
- Without Acks: at most 2 frames, then at most 2 more per second (the agent assumes lost acks after 1 s).
- Input still works.

Note: fixed after run 20261019-193627 – see the finding for D-VID-05 above.

#### D-VID-06 · Capture method
P3 · How: H · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** connected with control after Allow.

**Steps:**
1. Read Stats on the desktop.
2. Open the Ctrl+Alt+Del screen (SecureAttention); read Stats.

**Expected:**
- Desktop: `capture` "Desktop Duplication (GPU)" or "GDI".
- Secure desktop: `capture` "GDI".

#### D-VID-07 · No GPU encoder on a VM
P2 · How: H · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** normal lab VM without GPU-P; the changing window of D-VID-03 in the console session.

**Steps:**
1. Start with 60 fps; read Stats for 12 s.

**Expected:**
- Frames arrive (more than 20 in the 12 s); Stats `capture` without ", GPU encoder"; no Failed/Ended status.

#### D-VID-08 · GPU encoder
P2 · How: H · Needs: GPU-P · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** VM with a GPU-P adapter.

**Steps:**
1. Start with 60 fps ("Smooth", above 30); read Stats.
2. Settings 30 fps; read Stats and check for a key frame.

**Expected:**
- 60 fps: Stats `capture` ends in ", GPU encoder".
- 30 fps: software encoder (no ", GPU encoder"); the next frame is a key frame.

Note: there is no hook to force a GPU encoder failure; the software fallback can only be checked by switching to ≤ 30 fps or on a GPU whose encoder fails by itself.

#### D-VID-09 · N edition
P2 · How: H E · Needs: N · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** Windows N VM without the Media Feature Pack; own session.

**Steps:**
1. Harness client connects and waits 60 s.
2. Read Status, the agent log and the Application log.

**Expected:**
- Agent log: "Desktop: helper in session `<id>` ended (The screen can't be captured: The H.264 video encoder is not available on this device (Windows N editions need the Media Feature Pack)…) – starting it again".
- After 5 restarts in a minute: Status `Ended` "The device's screen can't be shown at the moment (it kept failing). Details are in the agent's log."
- Service still running.

Note: the sheet expects event 3100 and a clear error in RemoteHub. The code ends the helper with that text in the agent log only; RemoteHub gets the generic "kept failing" text and no 3100 event is written.

#### D-VID-10 · Dark display
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** `powercfg /change monitor-timeout-ac 1` in the guest; wait 70 s until the console screenshot is black.

**Steps:**
1. Harness client connects (own session).
2. Measure the time to the first non-black decoded frame.
3. Read the agent log.

**Expected:**
- Non-black picture within about 6 s.
- If dark for 3 s, the agent log has "no picture for 3 s (…)" with the wake result.

#### D-FIT-01 · Fit to window
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** own session at 1024×768 (or any size ≠ 1600×900). Harness `cad --nocad 1 --fit 1600x900` covers steps 1–2.

**Steps:**
1. Connect; send Settings (monitor 0, Fit 1600×900).
2. Read Stats `fit` and Layout; read the console's resolution from Hyper-V's video head (`Msvm_VideoHead`). Not from a PowerShell Direct session: its screen is its own (1024×768), not the console's.
3. Close the connection; read the resolution again.

**Expected:**
- Stats `fit` "1600×900 asked → 1600×900 (exact)" (or "same shape, smaller" / "closest listed mode"), followed by the display modes.
- Layout `width`×`height` = the new resolution.
- After the close: original resolution.

#### D-FIT-02 · Unsupported size
P3 · How: H · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** as D-FIT-01.

**Steps:**
1. Settings Fit 1234×777.
2. Read Stats `fit` and the resolution.

**Expected:**
- Stats `fit` names the result: "(exact)", "(same shape, smaller)" or "(closest listed mode)", or "… no display mode fits" / "… was refused by the display".
- Resolution = what `fit` says.

#### D-FIT-03 · Restore after a crash
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** fitted to 1600×900 as in D-FIT-01.

**Steps:**
1. PS Direct: `Stop-Process -Force` the helper.
2. Read the resolution (Hyper-V's video head, as in D-FIT-01) until it is fitted again (up to 30 s).
3. Close the connection; read the resolution.

**Expected:**
- Original resolution right after the kill (Windows undoes the temporary mode).
- After the close: original resolution.

Note: the service resends the Fit settings to the restarted helper, so while connected the resolution is fitted again a few seconds after the kill.

#### D-MON-01 · Two monitors
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** installed with the virtual display; agent.json `"virtualDisplay": "always"`, service restarted.

**Steps:**
1. Connect; send Settings with Fit 1600×900 (switches the virtual monitor on).
2. Wait for a new Layout.
3. Settings monitor 1, then monitor 0; save a frame after each.

**Expected:**
- Agent log "virtual monitor on at 1600×900".
- Layout `monitors` has 2 entries, the primary first.
- The frame changes with the monitor.

Note: with "always" the virtual monitor is only requested when a Fit size is sent (or the picture stays black); a plain Start does not switch it on.

#### D-MON-02 · All monitors
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** two monitors as in D-MON-01; marker app on the second monitor.

**Steps:**
1. Settings monitor −1.
2. Read Layout; save a frame.
3. Send a Mouse click at the marker app's position in the combined picture.

**Expected:**
- Layout `monitor` −1, size = bounding box of both monitors (scaled down by an integer factor above 4096×2304).
- Stats `capture` "GDI"; Fit is ignored for −1.
- The click lands on the marker app on the second monitor.

#### D-MON-03 · Monitor added / removed while connected
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** two monitors, monitor 1 shown.

**Steps:**
1. PS Direct: switch the virtual display device off (`pnputil /disable-device`).
2. Read Layout; save frames.
3. Switch it on again; read Layout.

**Expected:**
- New Layout with 1 monitor; shown index clamped to 0; frames continue.
- Layout with 2 monitors again; no Failed/Ended.

#### D-VD-01 · Virtual display auto
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** installed with the virtual display; `virtualDisplay` "auto"; guest display at 640×480.

**Steps:**
1. Connect; send Settings Fit 1600×900.
2. Read the agent log, Layout and the primary monitor.
3. Close; PS Direct checks the virtual display device state.

**Expected:**
- Agent log "virtual monitor on at 1600×900" and "virtual monitor `\\.\DISPLAYn` is the primary monitor now".
- Device disabled again after the close.

Note: "auto" asks for the virtual monitor only when the display's largest offered mode is smaller than the window (capped at 1280×720), or when the picture stays black. A Hyper-V display set to 640×480 still offers larger modes, so the condition is not met by the resolution alone.

#### D-VD-02 · Virtual display off
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** installed with the virtual display; `virtualDisplay` "off".

**Steps:**
1. Connect; send Settings Fit 1600×900.
2. Check the device state and the agent log.

**Expected:**
- Device stays disabled; no "virtual monitor" log line.

#### D-VD-03 · Two connections
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** as D-MON-01 ("always").

**Steps:**
1. Connection A: Fit 1600×900; connection B (second harness client, other session or same console): Fit 1600×900.
2. Close A; check the device.
3. Close B; check the device.

**Expected:**
- After A closes: virtual display still on.
- After B closes: switched off.

#### D-VD-04 · Resize
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** virtual monitor on at 1600×900 (D-MON-01).

**Steps:**
1. Settings Fit 1280×800.
2. Wait 6 s; read the primary monitor from the agent's last Layout (the virtual monitor is the primary one; the Hyper-V video head doesn't show it).

**Expected:**
- Resolution 1280×800 within 5 s; agent log "virtual monitor on at 1280×800"; `C:\VirtualDisplayDriver\vdd_settings.xml` has the new size.

### 9.9 Clipboard

#### D-CLIP-01 · Text to the device
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** connected with control to normaluser's session after Allow.

**Steps:**
1. Send Clipboard (type 7) "RH-CLIP-01 äöü €".
2. `Invoke-LabInSession` (normaluser): `Get-Clipboard`.

**Expected:**
- normaluser's clipboard has exactly that text.

#### D-CLIP-02 · Text from the device
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01.

**Steps:**
1. `Invoke-LabInSession`: `Set-Clipboard` with a Unicode text (emoji, CJK).
2. Wait for a Clipboard message.
3. Repeat with a text of 1,000,000 characters.

**Expected:**
- Clipboard message with the same text, both times (1,000,000 chars complete).

#### D-CLIP-03 · No echo
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01.

**Steps:**
1. Send Clipboard "RH-ECHO".
2. Listen 5 s for Clipboard messages.

**Expected:**
- No Clipboard message comes back.

#### D-CLIP-04 · Image both ways
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01.

**Steps:**
1. Send TransferStart (kind image, item "image.png") + TransferData; check formats in the session (`GetClipboardFormats`).
2. Same with "image.bmp".
3. In the session copy an image from Paint (and once a PNG from a browser).
4. Wait for the TransferOffer (with `transferOnPaste` the image is only offered); send TransferRequest with its id.
5. Receive TransferStart + TransferData; check the item name.

The automated test copies a bitmap with Windows Forms' `SetImage` (no "PNG" format), so it expects "image.bmp".

**Expected:**
- PNG: clipboard has "PNG" and CF_DIB; TransferEnd receipt `ok: true`, `toDevice: true`.
- BMP: clipboard has CF_DIB only.
- From the device: "image.png" when the program put PNG on the clipboard, else "image.bmp".

Note: the sheet says both arrive as PNG + CF_DIB and the device always sends PNG. The code puts a BMP only as CF_DIB, and sends BMP when the copying program offers no "PNG" format.

#### D-CLIP-05 · Files to the device
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** normaluser at the console with an Explorer window on an empty folder, focused; harness `copy --consent Ask --offer 30 --ctrlv 3` (after Allow) is the base; 3 files + a folder with 2 files on the host.

**Steps:**
1. Harness client: Start with `features: ["transferOnPaste"]`, consent Ask; Allow.
2. Send TransferOffer (files).
3. Send Ctrl+V (scan 0x1D + 0x2F); wait for TransferRequest with the id.
4. Send TransferStart + TransferData; wait for TransferEnd.
5. PS Direct: hash the pasted files; read owner of `C:\Users\normaluser\AppData\Local\Temp\RemoteHub Clipboard\<yyyyMMdd-HHmmss-fff>`.

**Expected:**
- TransferRequest arrives only after Ctrl+V.
- TransferEnd `ok: true`, `toDevice: true` (receipt).
- Files land in the clipboard folder (owner normaluser) and in the Explorer folder; SHA-256 equal.

#### D-CLIP-06 · Files from the device
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01; Start with `features: ["transferOnPaste"]`; files of known hashes in normaluser's profile.

**Steps:**
1. `Invoke-LabInSession`: `Set-Clipboard -Path <2 files and a folder>`.
2. Wait for TransferOffer; check names and sizes.
3. Wait 5 s (no TransferStart must come); send TransferRequest with the id.
4. Receive TransferStart + TransferData; write and hash.

**Expected:**
- Offer lists all items with relative paths and sizes; no data before the request.
- After the request the data comes; hashes equal.

#### D-CLIP-07 · Paste on demand
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-05; clipboard history on in normaluser's session.

**Steps:**
1. Send TransferOffer (an image).
2. Wait 30 s without pasting.
3. Read the agent log.
4. Paste in Paint; wait for TransferRequest.

**Expected:**
- No TransferRequest during the 30 s.
- Agent log may show "clipboard: svchost (…) reads every clipboard change – not sent (only a paste gets it)" (or rdpclip).
- TransferRequest only after the paste.

#### D-CLIP-08 · Limits
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01.

**Steps:**
1. Send TransferStart files with total 101 MB; then one with 1001 items. Both are sent as raw messages: RemoteHub – and the harness' `files` step – refuse such a copy before sending anything, so only a raw start reaches the agent's own check.
2. In the session copy a 101 MB file (and a folder with 1001 files).
3. Check the clipboard folder.

**Expected:**
- To the device: TransferEnd `ok: false` "Too much at once (at most 100 MB and 1000 files)."; nothing written.
- From the device: TransferEnd `ok: false` "Too much to copy through the remote desktop (at most 100 MB and 1000 files) – use the file transfer instead."

Note: the text from the device differs from the sheet's "Too much at once".

#### D-CLIP-09 · Path escape
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01.

**Steps:**
1. Send TransferStart (files) with an item `..\..\evil.txt`; send its data.
2. Same with `CON`.
3. Same with `C:\x\a.txt` and `/abs/b.txt`.
4. Search the profile and `C:\` for the files.

**Expected:**
- `..\..\evil.txt` and `CON`: TransferEnd `ok: false` "The device can't take the copied files: Unusable file name: …"; nothing written.
- `C:\x\a.txt` → `C\x\a.txt`, `/abs/b.txt` → `abs\b.txt`, both inside the clipboard folder.
- No file outside the clipboard folder.

#### D-CLIP-10 · Reparse points
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-06; a folder in normaluser's profile with a junction and a symlink to `C:\Windows`.

**Steps:**
1. Copy the folder in the session.
2. Read the TransferOffer items.

**Expected:**
- The junction and symlink (and anything below them) are not in the items.

#### D-CLIP-11 · Out of order / incomplete
P1 · How: H · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01.

**Steps:**
1. Send TransferStart (2 files) and TransferData with a wrong offset.
2. Second transfer: send only half the data, then TransferEnd (cancel).
3. Check the clipboard folders.

**Expected:**
- Wrong offset: TransferEnd `ok: false` "The device can't take the copied files: The copied data came out of order."
- Both: the transfer's folder is removed; no partial files.

Note: a short transfer gets no "incomplete" message; it stays open until cancelled or the connection ends, then its folder is removed. "The copied items are incomplete." is only for items without a path.

#### D-CLIP-12 · Files read with the user's rights
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-06; `C:\LabPrivate\secret.txt` readable only by Admin (folder listable by Users).

**Steps:**
1. In normaluser's session: `Set-Clipboard -Path C:\LabPrivate\secret.txt`.
2. Wait for the offer; send TransferRequest.
3. Collect TransferStart/Data/End.

**Expected:**
- TransferEnd `ok: false` "The copied files can't be read on the device: …" (access denied); no content bytes arrive.

#### D-CLIP-13 · Nobody signed in
P2 · How: H · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** nobody signed in; harness client connected to the sign-in screen (D-SIGNIN-01), no `features`.

**Steps:**
1. Send TransferStart (one small file) + data.
2. Read TransferEnd.

**Expected:**
- TransferEnd `ok: false` "The device can't take the copied files: Nobody is signed in on the device."

#### D-CLIP-14 · Policy levels
P1 · How: PS H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** normaluser at the console; one run per `DesktopClipboard` value 0, 1, 2, 3.

**Steps:**
1. Read Hello `clipboard`; connect after Allow.
2. Send Clipboard text, an image transfer and a files transfer.
3. Copy text, an image and a file in the session.

**Expected:**
- Hello `clipboard` = the level.
- 0: no text either way, image and files refused.
- 1: text both ways; image "Copying images isn't allowed on this device."; files "Copying files isn't allowed on this device."
- 2: text and image; files refused as above. 3: everything.

#### D-CLIP-15 · View-only has no clipboard
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** view-only connection to normaluser's session after Allow.

**Steps:**
1. Send Clipboard text and a TransferStart (image).
2. Copy text in the session.

**Expected:**
- Session clipboard unchanged; no Clipboard message arrives.
- TransferEnd `ok: false` "Copying images isn't allowed on this device."

#### D-CLIP-16 · Newer copy cancels
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-06 without `features` (data starts at once); a 90 MB file in the session.

**Steps:**
1. Copy the 90 MB file; wait for the first TransferData. The VM's link is held to 20 Mbit/s meanwhile (`Set-VMNetworkAdapter -MaximumBandwidth`, about 36 s for the file): unthrottled, 90 MB crossed the virtual switch before the second copy was made (run 20261019-193627).
2. Copy a small file while the first one transfers.
3. Collect TransferEnd messages.

The automated test makes both copies in one session task, 300 ms apart (`Set-Clipboard -Path`), with `features: []` at Start. Before that it sends one small file to the device, which is the transfer D-CLIP-18 needs.

**Expected:**
- First transfer: TransferEnd `ok: false` "A newer copy replaced it."
- Second transfer arrives complete.

#### D-CLIP-17 · Abort leaves nothing
P1 · How: H S N · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01; a 90 MB file on the host.

**Steps:**
1. Send TransferStart + half the TransferData.
2. Cut the VM's network; wait until the helper ends.
3. Restore; list the clipboard folders in normaluser's profile and the harness receive folder.

**Expected:**
- No transfer folder with partial files on the device; nothing partial on the harness side.

#### D-CLIP-18 · Old folders purged
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** as D-CLIP-01; PS Direct creates `…\RemoteHub Clipboard\20200101-000000-000` with creation time yesterday.

**Steps:**
1. Send a files transfer to the device.
2. List the clipboard root.

**Expected:**
- The old folder is gone; the new transfer folder exists.

### 9.10 Idle, reconnect, robustness

#### D-IDLE-01 · Idle timeout
P2 · How: PS H E · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** `DesktopIdleTimeoutMinutes=1`; own session.

**Steps:**
1. Connect; send no input.
2. Record the times of the Status messages.
3. Read the Application log.

**Expected:**
- At about 30 s: Status `Connected` "No mouse or keyboard input for a while – the session ends soon unless you use them."
- At about 60 s (±2 s): Status `Ended` "The session ended after 1 minute(s) without mouse or keyboard input (a policy of the device)."
- Event 2102 "Desktop: the session of `<acct>` from `<host IP>` ended after 1 minute(s) without input (policy)"; event 1101 with "(no mouse or keyboard input for 1 minute(s))".

#### D-IDLE-02 · Input resets
P3 · How: PS H · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** as D-IDLE-01.

**Steps:**
1. Connect; send a Key down/up (Shift) every 20 s for 3 min.

**Expected:**
- No warning, no `Ended`.

#### D-IDLE-03 · View-only counts
P3 · How: PS H · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** as D-IDLE-01; view-only Start.

**Steps:**
1. Connect view-only; send a Key every 20 s.

**Expected:**
- `Ended` after about 60 s (view-only input never counts); event 2102.

#### D-RES-01 · Resume after a drop
P1 · How: H N S E · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** connected to normaluser's session after Allow; token kept.

**Steps:**
1. Cut the VM's network for 10 s, then restore.
2. Harness client opens a new connection: Start with `resume` = token (same session, consent Ask).
3. Watch for a dialog; read Status and the Application log.

**Expected:**
- Status `Starting` "Continuing on the device…", then `Connected` with a new token; no `WaitingForUser`.
- Event 1103 "Desktop session continued: `<acct>` from `<host IP>` – … continued after a dropped connection (allowed by `<VM>\normaluser`)".
- If the agent had not noticed the drop yet, the old connection is taken over and gets no 1101; otherwise it has 1101 "(connection lost…)".

#### D-RES-02 · Token single use
P1 · How: H · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** as D-RES-01 after a successful resume.

**Steps:**
1. Abort the TCP connection.
2. Start again with the old (already used) token.

**Expected:**
- `WaitingForUser` (normal consent).

#### D-RES-03 · Token from another account
P1 · How: H · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** local admin `lapsadmin` created by the test; PSAdmin connected after Allow; PSAdmin's connection aborted (TCP).

**Steps:**
1. Harness client as lapsadmin: Start with PSAdmin's token.
2. Deny / close that attempt.
3. Within the minute: harness client as PSAdmin: Start with the token.

**Expected:**
- lapsadmin: `WaitingForUser` (token ignored).
- PSAdmin: resumes without a question; event 1103.

#### D-RES-04 · Token after 1 min
P2 · How: H N · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** as D-RES-01.

**Steps:**
1. Abort the TCP connection (no WebSocket close).
2. Wait 65 s.
3. Start with the token.

**Expected:**
- `WaitingForUser` (token expired).

Note: the harness must drop the connection without a WebSocket close frame; a normal close counts as a deliberate end and voids the token at once.

#### D-RES-05 · Token void after deliberate end
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** as D-RES-01; one run per end.

**Steps:**
1. End the session: (a) WebSocket close by the harness, (b) Disconnect on the banner, (c) idle timeout, (d) user refuses after an unlock (D-LOCK-03).
2. Start with the token within 10 s.

**Expected:**
- Every run: `WaitingForUser` (normal consent).

#### D-RES-06 · Different user signed in meanwhile
P1 · How: H S C · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** connected to normaluser's console session after Allow.

**Steps:**
1. Through the picture: Ctrl+Alt+Del, Switch user (normaluser's session is disconnected, the sign-in screen shows).
2. Abort the TCP connection; Start (console session, consent Ask) with the token – the connection continues on the sign-in screen.
3. When the picture asks for Ctrl+Alt+Delete (Server 2025 shows "Press Ctrl+Alt+Delete to unlock" there, Windows 11 the tiles), Ctrl+Alt+Del through the session first. Through the picture: choose Admin's tile, check that Admin's name now stands large in the middle, type the password.
4. Admin answers Deny in Admin's session (or the dialog's countdown denies it).

**Expected:**
- `WaitingForUser` "Waiting for `<VM>\Admin` to allow access…" on the resumed connection.

Note: OCR reads the small tile names on the wallpaper only in parts ("min" for Admin), so the test matches each line of the tile list to the closest of the device's accounts. When Admin's tile wasn't chosen the case fails on that (evidence `d-rob-res-user\tiles.png`, `selected.png`). Admin stays signed in afterwards: the next context starts from the file's state again (`Initialize-LabVm`) with that state's PIN.

#### D-RES-07 · Old connection still alive
P3 · How: H · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** connection A connected after Allow, kept open.

**Steps:**
1. Connection B: Start with A's token.
2. Watch A and B; read the Application log.

**Expected:**
- A is closed by the agent within about 5 s; B `Connected` without a question; event 1103.

Note: the sheet expects A to end with "continued in a new connection". The code keeps that only as an internal end reason: A gets no final Status, and the 1101 event (where the reason would be logged) is skipped for a taken-over connection.

#### D-ROB-01 · Helper killed
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** connected to normaluser's session after Allow.

**Steps:**
1. PS Direct: `Stop-Process -Force` the helper.
2. Watch Status and the session for a dialog; count frames.

**Expected:**
- Status `Connected` "The device's screen changed (sign-in or another session) – reconnecting…".
- New helper PID; frames continue; no `WaitingForUser`.

#### D-ROB-02 · Helper keeps failing
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** as D-ROB-01.

**Steps:**
1. Kill each new helper as soon as it appears, 6 times within a minute.
2. Read Status.

**Expected:**
- After the 6th failure: Status `Ended` "The device's screen can't be shown at the moment (it kept failing). Details are in the agent's log."
- Event 1101 with "(the device's screen kept failing)".

#### D-ROB-03 · Service restart while connected
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** connected with Fit 1600×900 (D-FIT-01).

**Steps:**
1. PS Direct: `Restart-Service RemoteHubAgent`.
2. Check helper processes, the resolution and `Get-LabAgentHealth`.

**Expected:**
- Harness connection closes; no `RemoteHubAgent.exe desktop` left; original resolution; agent healthy.
- Event 1101 with "(connection lost)".

#### D-ROB-04 · 8 h soak
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** connected (own session); moving marker window.

**Steps:**
1. Send a mouse move every minute for 8 h.
2. Sample the service's and helper's working set every 10 min.

**Expected:**
- Still `Connected` after 8 h.
- Agent service working set grows < 50 MB.

#### D-ROB-05 · Fuzzed messages
P2 · How: H · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** default.

**Steps:**
1. 200 connections: as first message send random bytes, random type bytes, Start with broken JSON.
2. 50 connected sessions: send random type bytes and broken JSON for Settings/Pin/Transfer messages.
3. After each batch: `Get-LabAgentHealth`; count events.

**Expected:**
- Each bad connection ends; agent stays healthy.
- Broken JSON in a connected session: event 1101 with "(invalid message from RemoteHub: …)"; unknown type bytes are ignored.

Note: a Start with broken JSON goes to the general error path: Status `Failed` "The remote desktop failed on the device. Details are in the agent's log." and one event 3100 per attempt (not a crash loop, but 3100 does appear). Malformed Mouse/Key messages are passed to the helper unchecked; the helper then ends and is restarted ("reconnecting…"), counting towards "kept failing".

#### D-ROB-06 · Ping
P3 · How: H · Needs: – · Status: ✅ Automated – `D-ROBUST.Tests.ps1`

**Preconditions:** connected (or waiting for consent).

**Steps:**
1. Send Ping (type 28) with 8 known bytes.
2. Read the answer.

**Expected:**
- Pong (type 29) with the same 8 bytes.

### 9.11 Audit

#### D-AUD-01 · Event IDs
P1 · How: E · Needs: – · Status: ✅ Automated – `D-ZAUDIT.Tests.ps1`

**Preconditions:** run after the D scenarios (and one PowerShell session and one wrong password for 1000/1001/2000).

**Steps:**
1. Collect the RemoteHub Agent events of the run.
2. Check each ID's text.

**Expected:**
- 1000, 1001 (PowerShell), 1100, 1101, 1102, 1103, 2000, 2100, 2101, 2102 each seen at least once.
- Desktop events name the account, source IP and session; 1100, 1101, 1103 and 2100 after a question also name view-only/control and the consent mode; 1101 the duration (hh:mm:ss).
- 1xxx Information, 2xxx Warning.

#### D-AUD-02 · No content
P1 · How: E · Needs: – · Status: ✅ Automated – `D-ZAUDIT.Tests.ps1`

**Preconditions:** marker strings typed (D-IN-01), copied (D-CLIP-01/02) and shown on screen during the run.

**Steps:**
1. Search the agent logs and the exported Application log for each marker string.

**Expected:**
- No marker string found; no keystrokes, clipboard text or screen content in logs or events.

## 10. RemoteHub on a Windows client (C, R)

RemoteHub itself is installed in a client VM and driven by UI Automation (FlaUI) in `normaluser`'s or `Admin`'s session.
Data checks read the files and the database through RemoteHub's own `LocalDb` classes in the harness where needed.
Unless a case says otherwise, "the MSI" is `RemoteHub-<version>-x64.msi` under test, "the data folder" is
`%LOCALAPPDATA%\RemoteHub` (database, `install-id`, `crash\`) and "the settings folder" is `%APPDATA%\RemoteHub`
(`local.json`, `lock.json`, `keys.dat`, `logs\remotehub.log`).

### 10.1 Installer

#### C-MSI-01 · Silent install
P1 · How: PS · Needs: – · Status: ✅ Automated – `C-MSI.Tests.ps1`

**Preconditions:** Fresh client VM from the newest checkpoint, no RemoteHub installed. The MSI and the agent MSI of the same build are copied to the guest.

**Steps:**
1. PS Direct as `PSAdmin`: run `msiexec /i RemoteHub-<ver>-x64.msi /qn /l*v C:\lab\rh-install.log` and wait for it.
2. Read the exit code.
4. Look for the Start menu shortcut `RemoteHub` in `%ProgramData%\Microsoft\Windows\Start Menu\Programs` and read its target and icon location (`WScript.Shell` `CreateShortcut`).
4. Look for the Start menu shortcut `RemoteHub` in `%ProgramData%\Microsoft\Windows\Start Menu\Programs`.
5. Compute SHA-256 of `C:\Program Files\RemoteHub\Agent\RemoteHub-Agent-x64.msi` and of the agent MSI of the same build.

**Expected:**
- Exit code 0.
- Shortcut `RemoteHub` exists, description "Remote desktop, PowerShell and admin tools in one place". It is a plain shortcut to `C:\Program Files\RemoteHub\RemoteHub.exe`, not an advertised one, and its icon is the exe's – not a file under `C:\Windows\Installer`, which the next upgrade removes (a copy pinned to the taskbar would then show a blank page).
- Shortcut `RemoteHub` exists, description "Remote desktop, PowerShell and admin tools in one place".
- `Agent\RemoteHub-Agent-x64.msi` exists and its SHA-256 equals the agent MSI under test.

#### C-MSI-02 · Starts without .NET installed
P1 · How: U · Needs: – · Status: ✅ Automated – `C-MSI.Tests.ps1`

**Preconditions:** C-MSI-01 done on a VM without any .NET desktop runtime (`dotnet --list-runtimes` fails or lists no `Microsoft.WindowsDesktop.App`).

**Steps:**
1. PS Direct: confirm no .NET desktop runtime is installed.
2. In `Admin`'s session (FlaUI): start `C:\Program Files\RemoteHub\RemoteHub.exe`.
3. Wait up to 60 s for a top-level window with title `RemoteHub`.
4. Look for any window from `dotnet` or a ".NET is required" prompt.

**Expected:**
- The main window `RemoteHub` appears (navigation and toolbar visible).
- No .NET runtime download prompt and no other window appears (the MSI is self-contained).

#### C-MSI-03 · .rdp association
P2 · How: PS · Needs: – · Status: ✅ Automated – `C-MSI.Tests.ps1`

**Preconditions:** C-MSI-01 done.

**Steps:**
1. PS Direct: read `HKLM\Software\Classes\RemoteHub.rdp\shell\open\command`.
2. Read value `RemoteHub.rdp` under `HKLM\Software\Classes\.rdp\OpenWithProgids`.
3. Read `HKLM\Software\RemoteHub\Capabilities\FileAssociations` value `.rdp` and `HKLM\Software\RegisteredApplications` value `RemoteHub`.
4. Read the default value of `HKCR\.rdp` and `HKCU\...\FileExts\.rdp\UserChoice` (in `Admin`'s hive).

**Expected:**
- Open command = `"C:\Program Files\RemoteHub\RemoteHub.exe" "%1"`.
- `OpenWithProgids\RemoteHub.rdp` exists (empty string).
- `FileAssociations\.rdp` = `RemoteHub.rdp`; `RegisteredApplications\RemoteHub` = `Software\RemoteHub\Capabilities`.
- `HKCR\.rdp` still points to mstsc's ProgId (`RDP.File`); no `UserChoice` set by the install.

#### C-MSI-04 · Upgrade from the previous release
P1 · How: PS U · Needs: – · Status: ✅ Automated – `C-MSI.Tests.ps1`

**Preconditions:** Previous release MSI installed. As `Admin`: one folder, two connections, one saved credential with the test password, one setting changed (e.g. theme Light); RemoteHub closed. Audit entry count noted.

**Steps:**
1. PS Direct: `msiexec /i <new MSI> /qn`, wait, read exit code.
2. List uninstall entries under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` with DisplayName `RemoteHub`.
3. In `Admin`'s session (FlaUI): start RemoteHub.
4. Check the tree, open *Settings → Security & storage → Manage saved credentials…*, check the theme.
5. Open *Settings → Logs → Open audit log…* and count entries; click *Verify integrity*.

2. List uninstall entries under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` with DisplayName `RemoteHub`, and check that the Start menu shortcut's icon file exists.
- Exit code 0; exactly one uninstall entry `RemoteHub` with the new version.
- Folder, both connections, the saved credential and the theme are unchanged.
- Audit entry count ≥ the noted count; *Verify integrity* shows "Audit log intact".

#### C-MSI-05 · Upgrade from RDP Manager 2.x
- Exit code 0; exactly one uninstall entry `RemoteHub` with the new version; the shortcut's icon file exists.

**Preconditions:** An RDP Manager 2.x build installed with settings in `%APPDATA%\RdpManager` (connections, `local.json`).

**Steps:**
1. PS Direct: `msiexec /i <RemoteHub MSI> /qn`, read the exit code and the msiexec log.
2. If refused: uninstall RDP Manager 2.x (`msiexec /x <product code> /qn`), then install the RemoteHub MSI.
3. Check the uninstall entries and `C:\Program Files\RemoteHub`.
4. In `Admin`'s session: start RemoteHub, check the tree.
5. PS Direct: compare `%APPDATA%\RdpManager` and `%APPDATA%\RemoteHub` file by file.

**Expected:**
- After install: one ARP entry `RemoteHub`, no RDP Manager entry; Start menu entry and folder named *RemoteHub*.
- `%APPDATA%\RemoteHub` holds copies of all files from `%APPDATA%\RdpManager`; `%APPDATA%\RdpManager` still exists unchanged.
- The connections from RDP Manager are shown.

Note: the MSI has the same upgrade code, but RDP Manager 2.x versions are higher than 0.x and `MajorUpgrade` refuses a downgrade ("A newer version of RemoteHub is already installed."). The README says to uninstall 2.x first. A direct upgrade only works from an RDP Manager build numbered below the RemoteHub version. The folder copy happens on the first start (`SettingsStore.MigrateFolder`), only if `%APPDATA%\RemoteHub` doesn't exist yet.

#### C-MSI-06 · Downgrade
P2 · How: PS · Needs: – · Status: ✅ Automated – `C-MSI.Tests.ps1`

**Preconditions:** MSI under test installed.

**Steps:**
1. PS Direct: `msiexec /i <previous release MSI> /qn /l*v C:\lab\downgrade.log`.
2. Read the exit code.
3. Search the log for the downgrade message.
4. Read the file version of `C:\Program Files\RemoteHub\RemoteHub.exe`.

**Expected:**
- Non-zero exit code (1603).
- The log contains "A newer version of RemoteHub is already installed."
- `RemoteHub.exe` still has the version under test.

#### C-MSI-07 · Uninstall
P2 · How: PS · Needs: – · Status: ✅ Automated – `C-MSI.Tests.ps1`

**Preconditions:** MSI installed; RemoteHub was started once as `Admin` with one connection saved.

**Steps:**
1. PS Direct: `msiexec /x <MSI> /qn`, read the exit code.
2. Check `C:\Program Files\RemoteHub`, the Start menu shortcut `RemoteHub`.
3. Check `HKLM\Software\Classes\RemoteHub.rdp`, `HKLM\Software\Classes\.rdp\OpenWithProgids\RemoteHub.rdp`, `HKLM\Software\RemoteHub\Capabilities`, `HKLM\Software\RegisteredApplications\RemoteHub`.
4. List `Admin`'s `%APPDATA%\RemoteHub` and `%LOCALAPPDATA%\RemoteHub`.

**Expected:**
- Exit code 0.
- Program folder, shortcut, ProgId and the four registry entries are gone.
- User data stays: `%APPDATA%\RemoteHub` (settings folder) and `%LOCALAPPDATA%\RemoteHub` (`remotehub.db`, `db.key`, `backup\`, `install-id`); `Videos\RemoteHub` if recordings exist. Nothing else is left.

#### C-MSI-08 · Install as standard user
P3 · How: S · Needs: – · Status: ✅ Automated – `C-MSI.Tests.ps1`

**Preconditions:** No RemoteHub installed. MSI copied to `C:\lab` (readable by everyone). `normaluser` signed in.

**Steps:**
1. Task in `normaluser`'s session: `msiexec /i C:\lab\RemoteHub-<ver>-x64.msi /qn /l*v C:\lab\user.log`, read the exit code.
2. Check `C:\Program Files\RemoteHub`.
3. Task in `normaluser`'s session: start the MSI interactively (`msiexec /i … `).
4. Screenshot the console.

**Expected:**
- Silent install fails with a non-zero exit code (per-machine package needs elevation); `C:\Program Files\RemoteHub` doesn't exist.
- The interactive start shows a UAC credential prompt on the secure desktop.

### 10.2 Storage, protection, audit

#### C-DB-01 · New database
P1 · How: U PS · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** RemoteHub installed, `Admin` never started it (no `%LOCALAPPDATA%\RemoteHub`).

**Steps:**
1. FlaUI in `Admin`'s session: start RemoteHub.
2. Add a connection `LabTarget` with user `PSAdmin` and the lab password; close RemoteHub.
3. PS Direct: read the first 16 bytes of `%LOCALAPPDATA%\RemoteHub\remotehub.db`.
4. Try to open `remotehub.db` with plain SQLite (no key) and run `SELECT * FROM nodes`.
5. Read the first 5 bytes of `db.key`.
6. Search every file under `%LOCALAPPDATA%\RemoteHub` and `%APPDATA%\RemoteHub` for the lab password (UTF-8 and UTF-16LE).

**Expected:**
- `remotehub.db` does not start with `SQLite format 3`; plain SQLite fails ("file is not a database").
- `db.key` starts with `RHDK` + 0x01 (DPAPI-protected, no startup protection).
- The lab password is found in no file.

#### C-DB-14 · No stored password in memory
P1 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** C-DB-01 done (`Admin` has the connection `LabTarget` with the lab password stored); RemoteHub closed.

**Steps:**
1. FlaUI in `Admin`'s session: start RemoteHub and wait for the main window. Don't open or connect `LabTarget`.
2. PS Direct as `PSAdmin`: write a full memory dump of `RemoteHub.exe` (`MiniDumpWriteDump`, full memory) to `C:\lab`.
3. Count the lab password in the dump as UTF-8 and as UTF-16LE, and the host name `labtarget.lab.test` as UTF-16LE.
4. Delete the dump; close RemoteHub.

**Expected:**
- The dump is written (more than 10 MB).
- The host name is found (the dump and the search work).
- The lab password is found neither as UTF-8 nor as UTF-16LE: a stored password stays encrypted in memory until a
  connection uses it (local-db-design.md §17).

#### C-DB-02 · Other Windows user
P1 · How: PS · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** C-DB-01 done (`Admin` has a database).

**Steps:**
1. PS Direct as `PSAdmin`: copy `Admin`'s `remotehub.db` and `db.key` to a folder `normaluser` can read, e.g. `C:\lab\dbcopy`.
2. Task in `normaluser`'s session: run the harness `LocalDb` open with `REMOTEHUB_DATA_DIR=C:\lab\dbcopy`.
3. Task in `normaluser`'s session: start RemoteHub with `REMOTEHUB_DATA_DIR=C:\lab\dbcopy`.

**Expected:**
- The harness open fails with `DatabaseUnreadableException` "The database key can't be read with this Windows account."
- RemoteHub shows "RemoteHub's database can't be opened: The database key can't be read with this Windows account. …"; no connection of `Admin` is shown.

#### C-DB-03 · Migration from settings.dat
P1 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** A 0.13 `settings.dat` made as `Admin` (folders, connections, saved credentials) placed in `%APPDATA%\RemoteHub`; no `%LOCALAPPDATA%\RemoteHub\remotehub.db`. The same data exported by the harness as JSON for comparison.

**Steps:**
1. FlaUI in `Admin`'s session: start RemoteHub, wait for the main window, close it.
2. PS Direct: list `%APPDATA%\RemoteHub`.
3. Harness: load the database with `LocalDb` / `AppDataRecords.Load` and compare with the JSON, field by field.
4. Open the audit log in RemoteHub and filter "Backups and storage".

**Expected:**
- `settings.dat` is renamed to `settings.dat.migrated`; `remotehub.db` and `db.key` exist in `%LOCALAPPDATA%\RemoteHub`.
- Every field matches (including saved credential passwords).
- Audit entry "Settings moved to the database": "<n> folders and connections, <m> saved credentials moved from settings.dat to the encrypted database."

#### C-DB-04 · Daily backup
P2 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** `Admin` has a database (C-DB-01); `backup\` empty.

**Steps:**
1. FlaUI: start RemoteHub, wait until the backup is written (a new file whose size has settled, at most 90 s), close it. PS Direct: list `%LOCALAPPDATA%\RemoteHub\backup`.
2. Start RemoteHub again the same day, wait 30 s (nothing to wait for), close it; list `backup\`.
3. Repeat 9 times: PS Direct moves the guest clock forward 1 day, start RemoteHub, wait until the new backup is written (at most 90 s), close. Time sync is off on both sides while the clock is moved: Hyper-V's time synchronisation service, and in the guest `w32time` and `vmictimesync` set to Disabled (only stopped, `w32time` is started again by its triggers and puts the clock back – lab run 20261016-212514). Their start types are restored afterwards.
4. List `backup\` and note the guest's clock (at the start, after the backup, at the end) after each round.

**Expected:**
- After step 1: one file `remotehub-<yyyyMMdd-HHmmss>.db` (UTC time).
- After step 2: still one file (less than 24 h since the newest).
- One new file per moved day; never more than 7 files; the oldest are removed first.

#### C-DB-05 · Corrupt database
P1 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** `Admin` has a database with at least one backup (C-DB-04) and one connection added after the backup.

**Steps:**
1. PS Direct: flip one byte in the middle of `remotehub.db` (RemoteHub closed).
2. FlaUI: start RemoteHub.
3. Read the message box and click *Yes*.
4. PS Direct: list `%LOCALAPPDATA%\RemoteHub`.
5. Check the tree in RemoteHub and the audit log.

**Expected:**
- Message: "RemoteHub's database can't be opened: The database is damaged or doesn't match its key. Restore the automatic backup of <date>? Changes made after it are lost. The current database is kept beside it." (Yes / No / Cancel).
- After *Yes*: the main window shows the data of the backup (the later connection is missing).
- `remotehub.db.damaged-<yyyyMMdd-HHmmss>` exists next to the restored `remotehub.db`.
- Audit entry "Database restored": "Restored the backup of <date>."

Note: the damaged database is kept as `remotehub.db.damaged-<stamp>` (with `-wal`/`-shm`), not `.unreadable-<stamp>`. The `.unreadable-<stamp>` copy is only made when the settings can't be read for another reason (e.g. an unreadable `settings.dat`).

#### C-LOCK-01 · Startup password
P1 · How: U PS · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** `Admin` has a database with one connection. PS Direct copies `db.key` to `C:\lab\db.key.old`.

**Steps:**
1. FlaUI: *Settings → Security & storage → Startup protection…*, enter a new password twice (at least 8 characters), click *Turn on*.
2. Close and start RemoteHub.
3. In "RemoteHub is locked", type a wrong password and press Enter.
4. Type the right password and press Enter.
5. Close RemoteHub. Harness: try to open the database with `C:\lab\db.key.old`.
6. PS Direct: check `lock.json` in `%APPDATA%\RemoteHub` and the first bytes of `db.key`.

**Expected:**
- After the restart the unlock window "RemoteHub is locked" ("Enter the startup password to open your connections.") appears before the main window.
- Wrong password: "Wrong password." stays in the window; audit entry "Unlock failed".
- Right password: main window with the connection; audit "RemoteHub unlocked" ("With the startup password").
- `lock.json` exists; `db.key` starts with `RHDK` + 0x02; the old key opens nothing (`DatabaseUnreadableException`).

#### C-LOCK-02 · Auto-lock
P2 · How: U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** C-LOCK-01 done. An RDP or PowerShell tab is open and connected.

**Steps:**
1. FlaUI: *Startup protection…* → enter the current password → *Lock when you're away* → *After 1 minute* (offered after *Never*, before 5, 15, 30 minutes and 1 hour).
2. Don't send any input to the VM for 1 minute + 30 s (RemoteHub checks every 15 s).
3. Look for RemoteHub windows.
4. Device side: check the session is still connected (PS Direct `query session` on the device).
5. Type the password in the unlock window.

**Expected:**
- The main window is hidden; "RemoteHub is locked" with "Locked after a while without input – your sessions keep running." and *Exit RemoteHub* is shown.
- The remote session is still active.
- After unlock the main window is back with the tab still connected; audit "RemoteHub locked" ("After 1 min without input", or 5 min).

#### C-LOCK-03 · Windows Hello PIN
P3 · How: U S · Needs: vTPM · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** VM with vTPM; `Admin` has a Windows Hello PIN.

**Steps:**
1. FlaUI: *Startup protection…*, enter a password twice, tick "Also unlock with Windows Hello (PIN, fingerprint or face)", *Turn on*.
2. Confirm the Hello prompt by typing the PIN (task in the session / console keyboard).
3. Restart RemoteHub.
4. In the unlock window, answer the Hello prompt with the PIN.

**Expected:**
- The unlock window says "Unlock with Windows Hello (PIN, fingerprint or face) or the startup password." and asks Hello right away.
- After the PIN the main window opens; audit "RemoteHub unlocked" ("With Windows Hello").

#### C-LOCK-04 · Turn off
P2 · How: U PS · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** C-LOCK-01 done; `backup\` has files; PS Direct copies `db.key` to `C:\lab\db.key.locked`.

**Steps:**
1. FlaUI: *Startup protection…*, enter the current password, click *Turn off startup protection*, confirm *Turn off*.
2. Restart RemoteHub.
3. PS Direct: check `lock.json`, `db.key` first bytes, `backup\`, `settings.dat.migrated`, `settings.dat.bak`.
4. Harness: try to open the database with `C:\lab\db.key.locked`.

**Expected:**
- RemoteHub opens without asking.
- `lock.json` is gone; `db.key` starts with `RHDK` + 0x01.
- Older backups, `.bak` and `settings.dat.migrated` are removed (only a new backup with the new key may exist).
- The copied old key opens nothing.

#### C-SHARE-01 · Shared folder on SMB
P2 · How: U · Needs: SMB · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** Host share `\\<host>\rhsettings` writable by `Admin`.

**Steps:**
1. FlaUI: *Settings → Security & storage → Change location…* → "Shared folder – e.g. OneDrive or a network share" → `\\<host>\rhsettings` → *Apply*; set a file password.
2. Add a connection; close RemoteHub.
3. Start RemoteHub again.
4. PS Direct: list `\\<host>\rhsettings` and `%APPDATA%\RemoteHub`.

**Expected:**
- `RemoteHub.settings` is created in the share.
- On the restart no password prompt "Unlock shared settings" appears; the connection is there.
- `%APPDATA%\RemoteHub\keys.dat` exists.

#### C-SHARE-02 · Sync conflict
P2 · How: U · Needs: SMB, 2VM · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** Two client VMs use the same shared folder (C-SHARE-01 on both).

**Steps:**
1. VM A (FlaUI): open a connection's editor and change its name, don't save yet.
2. VM B (FlaUI): rename another connection and save; wait until the shared file's SHA-256 changes (at most 30 s).
3. VM A: save the edit.
4. Read the dialog – its text through UI Automation, its title from the window (`GetWindowText`: RemoteHub's message boxes are named by their text in UI Automation and the title bar text isn't listed); click *Keep mine* on one run and *Reload* on a second run.
5. Compare the file content on both VMs after each choice.

**Expected:**
- VM B's save changes the shared file, on both runs.
- VM A shows "Settings changed on another device": "The settings file was changed on another PC (or by another admin) after RemoteHub loaded it. …" with *Reload* and *Keep mine*.
- *Reload*: VM B's change is shown, VM A's edit is gone. *Keep mine*: the file holds VM A's version.
- The file is never overwritten before the choice.

#### C-SHARE-03 · Share unavailable at start
P2 · How: U N · Needs: SMB · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** C-SHARE-01 done; SHA-256 of `RemoteHub.settings` noted.

**Steps:**
1. Cut the VM's network adapter (`Disconnect-VMNetworkAdapter`).
2. FlaUI: start RemoteHub, read the message, click OK.
3. Add a connection, close RemoteHub.
4. Restore the network; compute SHA-256 of `RemoteHub.settings`.

**Expected:**
- Message: "The shared settings file could not be opened: … RemoteHub starts with the settings stored on this PC. The shared file is not changed. Restart the app to try again."
- RemoteHub opens with the local settings.
- The shared file's SHA-256 is unchanged.

#### C-AUD-01 · Audit entries
P2 · How: U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** RemoteHub as `Admin` with one connection that has a stored password; a device for connect/transfer.

**Steps:**
1. FlaUI: connect and disconnect an RDP tab; edit the connection's host; delete another connection.
2. Trigger the password-prompt helper (R-RDP-04) so the password is copied.
3. Upload a file in a file transfer tab; record a tab for 5 s.
4. Open *Open audit log…*, export all entries to CSV.
5. Search the CSV for the password, the new host value and the file content.

**Expected:**
- Entries: "Connected", "Disconnected", "Changed" (lists field names, e.g. "Connection: Host"), "Deleted", "Password copied" ("For the Windows password prompt (cleared after 30 s)"), "Files transferred", "Recording started", "Recording saved".
- No password, no field value and no file content in any entry.

Note: there is no separate "copy password" command; passwords are only copied by the Windows password-prompt helper.

#### C-AUD-02 · Append-only
P1 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** Database with audit entries younger than the retention period; RemoteHub closed.

**Steps:**
1. Harness (with the DB key): `UPDATE audit SET detail='x' WHERE id=<n>`.
2. Harness: `DELETE FROM audit WHERE id=<n>`.
3. Harness: drop the trigger `audit_no_update` and change the `detail` of one entry (simulates an attacker with the key).
4. FlaUI: open the audit log, click *Verify integrity*.

**Expected:**
- Step 1 fails: "The audit log is append-only."
- Step 2 fails: "Only audit entries past the retention period can be deleted."
- *Verify integrity* shows "Audit log changed": "Entry <n> (<time>) was changed after it was written."

#### C-AUD-03 · Retention policy
P3 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** Database with audit entries older than 2 days (clock moved back while writing them, or harness inserts).

**Steps:**
1. PS Direct: set `HKLM\SOFTWARE\Policies\RemoteHub` DWORD `AuditRetentionDays` = 1.
2. FlaUI: start RemoteHub, wait 30 s.
3. Open the audit log; read the info line.
4. Harness: count entries older than 1 day.

**Expected:**
- Info line: "Entries are kept for 1 days (set by your organization)."
- No entry older than 1 day is left; newer entries stay.

#### C-AUD-04 · CSV export
P2 · How: U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** RemoteHub as `Admin`.

**Steps:**
1. FlaUI: add connections named `=cmd|'/c calc'!A1`, `+1`, `-2`, `@SUM(1)`.
2. Open the audit log, click *Export CSV…*, save as `C:\lab\audit.csv`.
3. PS Direct: read the CSV.

**Expected:**
- Header "Time (UTC),Event,Event code,Name,Details,Device,Account,Connection id".
- Each name starts with an apostrophe: `'=cmd…`, `'+1`, `'-2`, `'@SUM(1)`.
- Status line "<n> entries exported to C:\lab\audit.csv".

#### C-BAK-01 · Backup round trip
P1 · How: U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** `Admin` has folders, connections and a saved credential with a password.

**Steps:**
1. FlaUI as `Admin`: *Settings → Security & storage → Export backup*, save `C:\lab\rh.rhbackup`, enter a password twice.
2. FlaUI as `normaluser` (fresh profile): Import backup, pick the file, enter the password, choose *Add as folder*.
3. Compare the tree and credentials with `Admin`'s (harness, field by field).
4. Import again and choose *Replace everything*; compare again.

**Expected:**
- Export: "Backup saved to C:\lab\rh.rhbackup …"; audit "Backup exported".
- *Add as folder*: a folder "Imported <yyyy-MM-dd HH:mm>" with all items and the saved credential (password readable).
- *Replace everything*: the data equals `Admin`'s; audit "Backup imported" ("Added as a folder" / "Replaced all settings").

#### C-BAK-02 · Wrong backup password
P2 · How: U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** `C:\lab\rh.rhbackup` from C-BAK-01.

**Steps:**
1. FlaUI as `normaluser`: Import backup, pick the file.
2. Enter a wrong password.
3. Read the dialog – its text through UI Automation, its title from the window (`GetWindowText`; see C-SHARE-02) – then click OK; compare the tree before and after.

**Expected:**
- "Import failed" (the window title): "Wrong password or the backup file is damaged." (the text).
- Nothing imported; no "Backup imported" audit entry.

#### C-BAK-03 · Old `.rdpmbackup`
P3 · How: U · Needs: – · Status: ✅ Automated – `C-DATA.Tests.ps1`

**Preconditions:** A `.rdpmbackup` made with RDP Manager, its password known.

**Steps:**
1. FlaUI: Import backup; the file filter "RemoteHub backup (*.rhbackup;*.rdpmbackup)" shows the file.
2. Pick it, enter the password, choose *Add as folder*.
3. Compare with the source data.

**Expected:**
- Import works; folder "Imported <date>" holds the old connections and credentials.

### 10.3 Policies, updates, telemetry, crash reports

The mock (M) is a small HTTPS server on the host with a lab root certificate trusted in the VM and the
`update.remotehub.dev` name pointed at it through the VM's hosts file. It records every request for the checks.
RemoteHub must be a Release build (Debug builds never send telemetry).

#### C-POL-01 · `DisableTelemetry`
P1 · How: PS U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock running; telemetry on in RemoteHub (default).

**Steps:**
1. PS Direct: set `HKLM\SOFTWARE\Policies\RemoteHub` DWORD `DisableTelemetry` = 1.
2. FlaUI: start RemoteHub, wait 30 s, close.
3. Read the mock's request log.
4. Start RemoteHub, open *Settings → Updates & privacy*.

**Expected:**
- No POST to `/telemetry`.
- The usage data toggle is off and disabled; text "Turned off by your organisation."

#### C-POL-02 · `DisableUpdateCheck`
P1 · How: PS U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock running.

**Steps:**
1. PS Direct: set DWORD `DisableUpdateCheck` = 1 under `HKLM\SOFTWARE\Policies\RemoteHub`.
2. FlaUI: start RemoteHub, wait 30 s.
3. Read the mock's request log.
4. Open *Settings → Updates & privacy*.

**Expected:**
- No GET to `/latest`.
- Update check toggle and *Check for updates now* disabled; "Some of these settings are managed by your organisation."

#### C-POL-03 · `UpdateChannel`
P2 · How: PS U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock running; RemoteHub 0.x (default channel Beta).

**Steps:**
1. FlaUI without policy: start RemoteHub, wait 30 s; note the request URL.
2. PS Direct: set string `UpdateChannel` = `stable`.
3. Restart RemoteHub, wait 30 s; note the request URL.
4. Open *Settings → Updates & privacy*.

**Expected:**
- Without policy: GET `/latest?channel=beta` (and `/latest`, because Beta also checks GA).
- With policy: GET `/latest` (no `?channel=beta`).
- GA (stable) is selected, both channel buttons disabled; "Some of these settings are managed by your organisation."

#### C-POL-04 · `SettingsFolder`
P2 · How: PS U · Needs: SMB · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Host share `\\<host>\rhsettings`.

**Steps:**
1. PS Direct: set string `SettingsFolder` = `\\<host>\rhsettings` under `HKLM\SOFTWARE\Policies\RemoteHub`.
2. FlaUI: start RemoteHub; set the file password when asked.
3. Open *Settings → Security & storage → Change location…*.
4. Open *Settings → Help & about → Settings overview…*.

**Expected:**
- `RemoteHub.settings` is used in the share.
- The location window says "The settings location is managed by your organization: \\<host>\rhsettings"; the location can't be changed.
- Overview "Stored": "Shared folder (set by your IT policy)".

#### C-POL-05 · `DisableRecording`
P2 · How: PS U · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** A connection with RDP and PowerShell (agent or WinRM) available.

**Steps:**
1. PS Direct: set DWORD `DisableRecording` = 1.
2. FlaUI: start RemoteHub; open RDP, agent desktop, PowerShell, file transfer, registry, services and processes tabs.
3. In each tab: look for the button "Record this tab to a video file" and the Record entry in the tab's right-click menu.
4. Open *Settings → Recordings*.

**Expected:**
- No Record button or menu entry in any tab.
- Settings: "Recording is turned off by your organisation."; all recording controls disabled.

#### C-POL-06 · `RecordingFolder` with `%USERPROFILE%`
P2 · How: PS U · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** A connection with a PowerShell tab available.

**Steps:**
1. PS Direct: set string `RecordingFolder` = `%USERPROFILE%\LabRecordings`.
2. FlaUI: open *Settings → Recordings*.
3. Record a PowerShell tab for 5 s, stop.
4. PS Direct: list `C:\Users\Admin\LabRecordings`.

**Expected:**
- Settings show `C:\Users\Admin\LabRecordings (set by your organisation)`; folder buttons disabled.
- The MP4 is in `C:\Users\Admin\LabRecordings`.

#### C-POL-07 · HKCU vs HKLM, legacy `RdpManager` key
P3 · How: PS U · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock running; host share available.

**Steps:**
1. PS Direct: HKLM DWORD `DisableRecording` = 0, HKCU (Admin's hive) `DisableRecording` = 1, `DisableUpdateCheck` = 1 (its bar would move the list while the tab is opened); restart RemoteHub; double-click *Local* and wait until its tab is open (the main window is named after it).
2. Remove the `Policies\RemoteHub` recording values; set `HKLM\SOFTWARE\Policies\RdpManager` `SettingsFolder` to a folder without a settings file; restart.

**Expected:**
- Step 1: the tab is open and shows *Record this tab to a video file* (HKLM wins, also with 0).
- Step 2: the warning names the legacy key's folder (`SettingsFolder` is read from `Policies\RdpManager`).

Note: only `SettingsFolder` is read from the legacy `Policies\RdpManager` key (`SettingsStore.PolicyFolder`). All other policies (`AppPolicy`: telemetry, updates, channel, recording, audit retention) read only `Policies\RemoteHub`, HKLM first, then HKCU.

#### C-UPD-01 · Update offered
P2 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock answers `/latest` with JSON `{version, notes, download{name,url,sha256,size}}`.

**Steps:**
1. Mock: version = running version + 1 patch. FlaUI: start RemoteHub, wait 10 s.
2. Read the bar at the top; click *Details…*.
3. Mock: version = running version. Restart, wait 10 s.
4. Mock: version = running version − 1 patch. Restart, wait 10 s.

**Expected:**
- Step 1: bar "RemoteHub <new> is available – you have <current>." with *Details…*; the window shows "RemoteHub <new> is available", *Download and install*, *Skip this version*, *Later*.
- Steps 3 and 4: no bar.

#### C-UPD-02 · Checksum mismatch
P1 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock offers a higher version with a wrong `sha256` (correct size), download from an allowed URL served by the mock.

**Steps:**
1. FlaUI: open the update window, click *Download and install*.
2. Read the error text.
3. PS Direct: list `%TEMP%\RemoteHub\Updates` of `Admin`.
4. Check for an msiexec process and the installed version.

**Expected:**
- Error: "The downloaded file doesn't match the published SHA-256 checksum. It was deleted and won't be installed." plus "You can download the installer from the releases page instead."; *Open releases page* shown.
- No `.msi` and no `.partial` file left; no msiexec started; version unchanged.

#### C-UPD-03 · Untrusted host
P1 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock offers a higher version with `download.url` = `https://evil.lab/RemoteHub.msi`; second run `https://github.com/someone/other/releases/download/v1/x.msi`; third run `http://update.remotehub.dev/x.msi`.

**Steps:**
1. FlaUI: open the update window, click *Download and install*.
2. Read the error; check the mock / network log for a request to the URL.
3. Repeat with the second and third URL.

**Expected:**
- Each time: "The download address isn't trusted (only RemoteHub's own releases on GitHub or remotehub.dev, over HTTPS)."
- No request to the download URL is made.

Note: only `remotehub.dev` (and subdomains) or release assets of `GrischaE1/RemoteHub` on `github.com` / `api.github.com` are allowed, over HTTPS. Other GitHub paths are refused too. After redirects the final host must be `github.com`, `githubusercontent.com` or `remotehub.dev`.

#### C-UPD-04 · Install
P2 · How: U C M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock offers the next build's MSI with the right SHA-256 and size. An RDP tab is open.

**Steps:**
1. FlaUI: *Details…* → *Download and install*.
2. Confirm the question about open sessions.
3. Console (C): approve the UAC prompt on the secure desktop by keyboard (`Send-LabKeyCombo` Alt+Y).
4. Wait up to 3 min for `RemoteHub.exe` to run again.
5. Read the file version of `C:\Program Files\RemoteHub\RemoteHub.exe`.

**Expected:**
- "Download verified (SHA-256)." then RemoteHub closes.
- `%TEMP%\RemoteHub\Updates\install-update.cmd` runs `msiexec /i … /passive /norestart`.
- RemoteHub starts again from Program Files with the new version.

#### C-UPD-05 · Mandatory / skip
P3 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock offers a higher version.

**Steps:**
1. FlaUI: *Details…* → *Skip this version*. Close RemoteHub as a user does (it saves the skip 600 ms later or on exit – killing it at once could lose it), start it again, wait 10 s.
2. Mock: same version with `mandatory: true`. Restart.
3. Mock: `mandatory: false`, `minSupportedVersion` above the running version. Restart.

**Expected:**
- Step 1: no bar after the restart.
- Steps 2 and 3: the update window opens by itself with "This update is required – this version is no longer supported."; no *Skip this version* button.

#### C-TEL-01 · Telemetry content
P1 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock running; telemetry on; no policy.

**Steps:**
1. FlaUI: start RemoteHub, wait 15 s.
2. Read the POST body to `/telemetry` from the mock.
3. PS Direct: read `%LOCALAPPDATA%\RemoteHub\install-id`.
4. Repeat on each client VM.

**Expected:**
- JSON with exactly the keys `event`, `version`, `channel`, `os`, `arch`, `installId`; `event` = `app_start`.
- `os` = `Windows 11` on W11 VMs (build ≥ 22000), `Windows 10` on W10 VMs; `arch` = `x64`.
- `installId` = the GUID in `install-id`; no host name, user name or address in the body.

#### C-TEL-02 · Off deletes install ID
P2 · How: U PS · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** `install-id` exists (C-TEL-01).

**Steps:**
1. FlaUI: *Settings → Updates & privacy*, turn usage data off, save.
2. PS Direct: check `%LOCALAPPDATA%\RemoteHub\install-id`.
3. Turn it on again, restart RemoteHub; read the new `install-id`.

**Expected:**
- After step 1 the file is gone.
- After step 3 a new, different GUID.

#### C-CRASH-01 · .NET crash
P2 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock running; telemetry on. A way to throw an unhandled exception in RemoteHub (see Note), with a message containing the user name, computer name, `C:\Users\Admin\x.txt` and `10.0.0.5`.

**Steps:**
1. Trigger the crash; wait for RemoteHub to end.
2. PS Direct: list `%LOCALAPPDATA%\RemoteHub\crash` and read the `crash-*.json`.
3. FlaUI: start RemoteHub, wait 15 s.
4. Read the mock's POSTs to `/telemetry`; list `crash\` again.

**Expected:**
- One `crash-<ticks>-<guid>.json` with `ErrorType`, redacted `Message` and `Stack`.
- Mock receives `app_start` and `app_crash` with `errorType`, `message`, `stack`.
- Message shows `[user]`, `[computer]`, `[path]`, `[ip]` instead of the values; at most 500 / 8,000 characters.
- The crash file is deleted after it was sent.

Note: RemoteHub has no built-in switch to force a crash; the lab needs its own trigger (e.g. a test build hook). An "unexpected error" that RemoteHub survives ("RemoteHub – unexpected error" message box) is recorded the same way, at most three per session.

#### C-CRASH-02 · Unexpected exit
P3 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Preconditions:** Mock running; telemetry on.

**Steps:**
1. FlaUI: start RemoteHub; PS Direct checks `crash\session.json` exists.
2. PS Direct: `Stop-Process -Name RemoteHub -Force` (as Task Manager does).
3. Start RemoteHub, wait 15 s.
4. Read the mock's POSTs.

**Expected:**
- An `app_crash` with `errorType` = `UnexpectedExit` (no Application Error 1000 event for RemoteHub.exe).
- After a clean close, `session.json` is gone and the next start sends no `app_crash`.

### 10.4 RDP through RemoteHub (2VM)

RemoteHub runs in the client VM (`Admin`'s session, FlaUI); the device is `Server-2025-en-US` unless noted.

#### R-RDP-01 · Connect with saved credentials
P1 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** Connection `Device` to the device's address with a saved credential `PSAdmin` + lab password. NLA delegation for the device allowed (or a domain).

**Steps:**
1. FlaUI: double-click `Device`.
2. Wait up to 60 s for the tab status dot to turn green ("Connected").
3. PS Direct on the device: `query session`.
4. Screenshot the tab.

**Expected:**
- Tab "Remote desktop" shows the device's desktop; status "Connected".
- `query session` lists a new Active RDP session for `PSAdmin`.

#### R-RDP-02 · Local account `.\user`
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** Credential with user `.\PSAdmin` (or ticked "Local account on the target computer (.\user)").

**Steps:**
1. FlaUI: connect.
2. PS Direct on the device: `query session` and `whoami` in the new session (task in that session).

**Expected:**
- Signed in as `<DEVICE>\PSAdmin` (local account of the device).

#### R-RDP-03 · Saved credentials without the NTLM delegation policy
P3 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** Non-domain device; on the client no "Allow delegating saved credentials with NTLM-only server authentication" policy; NLA on.

**Steps:**
1. FlaUI: connect with the connection's stored password.
2. Read the tab's status text and any Windows dialog; list the sessions on the device.

**Expected:**
- The tab connects and the device has a new RDP session for the account; no *Windows Security* prompt and no message about saved credentials.
- The evidence records CredSSP's built-in defaults on the client (`HKLM\SYSTEM\CurrentControlSet\Control\Lsa\Credssp\PolicyDefaults`).

Note: RemoteHub hands the password to the RDP control itself (`ClearTextPassword`). Windows doesn't treat it as a password saved in Credential Manager, so the policy mstsc needs for those isn't needed (lab runs 20261017-160133 and 20261019-193627, Windows 11 26H2 client). Should Windows refuse after all, the tab shows Windows' own text (`GetErrorDescription`); the README explains the policy.

#### R-RDP-04 · Password prompt helper
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** Device with "Always prompt for password upon connection" set; connection with stored password; *When Windows asks for the password* = "Copy to clipboard (cleared after 30 s)" (default).

**Steps:**
1. FlaUI: connect; wait for the Windows Security dialog ("Credential Dialog Xaml Host").
2. Task in `Admin`'s session: read the clipboard text and the formats `CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard`.
3. Wait 35 s without logging in; read the clipboard again.
4. Reconnect; paste with Ctrl+V into the dialog, OK; read the clipboard right after login.

**Expected:**
- Clipboard holds the password; `CanIncludeInClipboardHistory` = 0, `CanUploadToCloudClipboard` = 0; a notice at the bottom of the window.
- After 30 s (step 3) and right after login (step 4) the clipboard is empty.
- Audit "Password copied" ("For the Windows password prompt (cleared after 30 s)").

#### R-RDP-05 · Clipboard / drive redirection
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** Connection with "Clipboard (copy & paste)" = Yes, "Local drives" = No.

**Steps:**
1. Copy a text in Notepad on the client; paste in Notepad in the RDP session (task on the device reads it).
2. Copy a text on the device; paste in Notepad on the client.
3. Task on the device (in the RDP session): `Test-Path \\tsclient\C`.
4. Set "Local drives" = Yes, reconnect, repeat step 3.

**Expected:**
- Text arrives both ways unchanged.
- `\\tsclient\C` is missing with drives off and present with drives on.

#### R-RDP-06 · Dynamic resolution
P3 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** "Resize behaviour" = "Dynamic resolution" (default).

**Steps:**
1. FlaUI: connect; read the session resolution on the device (task in the session).
2. Resize the RemoteHub window (e.g. 1280×800 → 1600×900).
3. Wait 5 s; read the resolution again.

**Expected:**
- The device session's resolution follows the tab's client size (width even, height as the tab).

#### R-RDP-07 · Ctrl+Alt+Del, full screen, detach/dock
P3 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RDP tab connected.

**Steps:**
1. FlaUI: click "Send Ctrl+Alt+Del to the session"; screenshot the tab; Esc.
2. Click "Full screen"; check the RDP connection bar; leave full screen.
3. Click "Detach into its own window / dock back"; check the separate window; click again to dock.
4. Click into the session; check the frame header.

**Expected:**
- The device shows its security options screen in the tab.
- Full screen works with the native connection bar.
- Detach opens a separate resizable window; dock brings the tab back.
- The frame lights up in the session colour with "Keyboard → remote session".

#### R-RDP-08 · .rdp import/export
P2 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** Working connection `Device` with drives and clipboard redirection on.

**Steps:**
1. FlaUI as `Admin`: right-click `Device` → *More* → *Save as .rdp file…*, include the password, save `C:\lab\device.rdp`.
2. As `normaluser`: double-click `C:\lab\device.rdp` (RemoteHub is the handler via "Open with").
3. Read the "Open .rdp file" dialog; click *Connect*.
4. Enter the password when Windows asks.

**Expected:**
- The dialog says "Connect to <host>? The connection was added from a .rdp file. …" and lists "Shares this PC's drives with the server." and "Shares the clipboard with the server."
- The session connects.

Note: the password in the `.rdp` is DPAPI-protected for the exporting Windows user (like mstsc), so `normaluser` can't read it and Windows asks for it. The risks dialog is shown only when a `.rdp` file is opened (argument / file association) and creates a new entry, not for import through *New* or drag and drop.

#### R-RDP-09 · Single instance
P3 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RemoteHub running with connection `Device`; `C:\lab\device.rdp` for the same host and port.

**Steps:**
1. Run `RemoteHub.exe C:\lab\device.rdp` in `Admin`'s session.
2. Count `RemoteHub.exe` processes and main windows.
3. Count connections for that host in the tree.

**Expected:**
- Still one RemoteHub process and window; it comes to the front.
- No new entry; the existing `Device` entry connects.

#### R-AGENT-01 · Full agent flow from the UI
P1 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** Device with WinRM working for the connection (remoting check passed); no agent on the device.

**Steps:**
1. FlaUI: right-click the connection → *Tools* → *Test PowerShell remoting* first and wait for "PowerShell remoting works (…)" – the install dialog offers the silent install only once WinRM is known to work, and *Tools* lists the tools only after a passed test.
2. Right-click the connection → *RemoteHub Agent* → *Install RemoteHub Agent…*; choose "Install silently over PowerShell remoting (WinRM) …", keep "Use the agent for this connection afterwards", click *Install*; wait for the progress window to show *Done*.
3. Right-click → *Tools* → *PowerShell* (if *Tools* shows *Test RemoteHub Agent* instead, run that first); run `hostname`.
4. From *Tools*, open *File transfer* and upload a file; open *Registry*, *Services*, *Processes*.
5. Right-click → *Connect* (with the agent in use, the agent desktop tab); on its connect page keep the console session and click *Connect*.

**Expected:**
- Progress ends with "Installed – service Running." and *Done*; the connection shows the agent shield.
- PowerShell prints the device's host name; each tool tab loads data over the agent.
- The agent desktop tab "Desktop (agent)" shows the device screen.

#### R-AGENT-02 · Automatic → agent desktop
P2 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent installed (R-AGENT-01); "Remote desktop through" = "Automatic – the agent if installed, otherwise RDP".

**Steps:**
1. FlaUI: double-click the connection.
2. Check the tab type.
3. Click *Use RDP instead* in the tab.

**Expected:**
- The agent desktop tab opens (header "Remote desktop through the RemoteHub Agent").
- *Use RDP instead* opens an RDP tab to the same device.

#### R-AGENT-03 · Own RDP session warning
P3 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent installed; an RDP tab from this RemoteHub to the device is connected.

**Steps:**
1. FlaUI: open the agent desktop tab.
2. In the session picker, select the session that the RDP tab uses.
3. Read the warning text.

**Expected:**
- Warning: "This session is connected by RDP from this PC (<client>). Its RDP window moves your mouse to follow the remote pointer – minimize the RDP window while you control it here, or connect from another PC."

### 10.5 End to end through RemoteHub's window (2VM)

The admin's side is RemoteHub in the client VM, driven by FlaUI in `Admin`'s session.
- The keyboard and clipboard on the admin's PC are real: Ctrl+C / Ctrl+V go to RemoteHub's window through the Hyper-V console keyboard or SendInput in that session.
- The device side is checked with PS Direct and in-session helpers on the device VM.
- Each case runs against two devices: a **client device** (the second client VM, e.g. W10 against W11) and `Server-2025-en-US`.

**Agent connection**

#### UI-AG-01 · Install from the UI
P1 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** Connection to the device with WinRM working (remoting check passed); no agent on the device.

**Steps:**
1. FlaUI: right-click the connection → *Tools* → *Test PowerShell remoting* first; wait for "PowerShell remoting works (…)" (the silent install is offered only once WinRM is known to work). Then right-click → *RemoteHub Agent* → *Install RemoteHub Agent…*.
2. Choose "Install silently over PowerShell remoting (WinRM) – copies both files to a temp folder, installs, deletes them"; click *Install*.
3. Wait for the progress window; read each row.
4. Hover the agent shield next to the connection name.
5. PS Direct on the device: `Get-Service RemoteHubAgent`; open the audit log in RemoteHub.

**Expected:**
- Rows end with "Installed – service Running." and the button *Done*.
- The shield's tooltip shows the agent version.
- Service `RemoteHubAgent` running; audit "Agent installed" with detail "Installed – Remoting".

#### UI-AG-02 · Test RemoteHub Agent
P1 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** Agent installed; connection set to "Connect through" = "RemoteHub Agent"; remoting status reset (not yet tested).

**Steps:**
1. FlaUI: right-click the connection → *Tools* → *Test RemoteHub Agent*.
2. Read the dialog.

**Expected:**
- Dialog "RemoteHub Agent": "The RemoteHub Agent works (<version info>). Right-click the connection or its tab → PowerShell, File transfer, Registry, Services or Processes."

Note: *Test RemoteHub Agent* is only in the *Tools* group while the connection uses the agent and the last result isn't "available"; after a successful test the group shows the tools instead.

#### UI-AG-03 · Wrong pin
P1 · How: U PS E · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** Agent installed and pinned. The device re-enrolled with another certificate (install again from a second RemoteHub / harness with a new identity).

**Steps:**
1. Note the device's Security log 4624/4625 count and the agent log size.
2. FlaUI: open a PowerShell tab through the agent (right-click → *Tools* → *PowerShell*; when *Tools* offers *Test RemoteHub Agent* instead, run that – its answer names the certificate the same way).
3. Read the error; look for a way to continue anyway.
4. PS Direct on the device: read the Security log and the agent log again.

**Expected:**
- Error: "The RemoteHub Agent on <host>:58732 presented a different certificate than the one stored for this device (expected …<8 hex>, got …<8 hex>). The connection was stopped before any credentials were sent. …"
- No button to accept the new certificate.
- No new 4625 / 4624 for the account and no sign-in line in the agent log.

#### UI-AG-04 · Wrong password / not allowed
P2 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent installed.

**Steps:**
1. FlaUI: set the connection's credential to `PSAdmin` with a wrong password; open a PowerShell tab.
2. Read the message.
3. Set the credential to `normaluser` with the right password; open a PowerShell tab.
4. Read the message.

**Expected:**
- Wrong password: "The RemoteHub Agent on <host>:58732 rejected the sign-in: wrong user name or password, or the account is disabled, locked or its password expired. …"
- `normaluser`: "The account is not allowed to use the RemoteHub Agent on <host>:58732: it must be an administrator there …"

#### UI-AG-05 · Agent stopped
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** Agent installed; connection uses the agent.

**Steps:**
1. FlaUI: right-click → *Tools*: PowerShell is offered (else *Test RemoteHub Agent* first). PS Direct on the device: `Stop-Service RemoteHubAgent`.
2. FlaUI: open a PowerShell tab; time how long until a message shows.
3. PS Direct: `Start-Service RemoteHubAgent`.
4. FlaUI: *Reconnect* the tab (or open it again).

**Expected:**
- The tab reports a connection failure (e.g. "Could not connect to the RemoteHub Agent on <host>:58732: …" or "No answer from a RemoteHub Agent on <host>:58732 in time. …") within 30 s; RemoteHub stays responsive.
- After the start the tab connects.

#### UI-AG-06 · Update from the UI
P1 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Previous agent version installed (`Get-LabAgentMsi -Previous`); RemoteHub ships the new agent MSI.

**Steps:**
1. FlaUI: right-click → *RemoteHub Agent* → *Update RemoteHub Agent…*.
2. Choose "Update through the running agent"; leave "Enroll again" unticked; click *Update*.
3. Wait for *Done*.
4. Hover the shield; PS Direct on the device: read the agent's file version and certificate thumbprint.

**Expected:**
- Last row "Updated – RemoteHub Agent <new version> is running."
- Shield tooltip shows the new version; the device keeps its certificate (same SHA-256).

**Agent remote desktop**

#### UI-AD-01 · Open
P1 · How: U C · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent installed; `normaluser` signed in on the device console.

**Steps:**
1. FlaUI: right-click → *Connect* (with the agent installed, the agent desktop; *Remote desktop via RDP* is the other way).
2. Read the session list.
3. Pick the console session, choose a consent mode the test can answer, click *Connect*.
4. Take a Hyper-V console screenshot of the device and a screenshot of the tab picture.
5. Compare both images (scaled).

**Expected:**
- Status "Signed in as <account>. Choose what to show."; the list shows "Console – …" and any other sessions.
- The tab reaches Connected.
- The tab picture matches the console screenshot (similarity above the threshold).

#### UI-AD-02 · Control
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** UI-AD-01 connected; Notepad open on the device (task in the user's session).

**Steps:**
1. FlaUI: click into the tab over Notepad.
2. Type `RemoteHub lab 123 !@#` through the console keyboard / SendInput.
3. Task on the device: read Notepad's text.
4. On the de-DE VM: type `äöüß<>|@€`.

**Expected:**
- Notepad on the device holds exactly the typed text, also the German characters.

#### UI-AD-03 · Monitor / All monitors / Resolution menu
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Device with two monitors (or a virtual second display); agent desktop connected.

**Steps:**
1. Note the device's resolution (PS Direct).
2. FlaUI: pick "Monitor 2 · <w>×<h>" in the monitor box; then "All monitors · <w>×<h>".
3. In the resolution menu pick "The device's own resolution", then "Fit to this window"; resize the RemoteHub window; read the device resolution.
4. Pick "3840 × 2160"; read the device resolution; back to "Fit to this window".
5. Disconnect; read the device resolution.

**Expected:**
- Each choice changes the picture to that monitor / all monitors.
- With Fit to this window the device resolution follows the tab (as in D-MON / D-FIT); with the device's own it stays as it was.
- With 3840 × 2160 the device runs wider than the tab (that size or the nearest it offers) and the picture is scaled down to fit.
- After disconnect the original resolution is back.

#### UI-AD-04 · Key combinations
P2 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop connected; "Key combinations" on (default).

**Steps:**
1. Click into the tab; press Alt+Tab; task on the device reads the foreground window.
2. Press Win+R; check for the Run dialog on the device.
3. Press Win+L; check the client VM's session state and the device.

**Expected:**
- Alt+Tab and Win+R act on the device, not the client.
- Win+L locks the client VM, the device stays unlocked.

#### UI-AD-05 · Ask
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** `normaluser` signed in on the device; connect as `PSAdmin`.

**Steps:**
1. FlaUI: connect with "Ask them to allow it".
2. Task in `normaluser`'s session (UIA): click Allow in the RemoteHub consent window.
3. Check the tab state.
4. Disconnect; connect again; this time click Deny.

**Expected:**
- Allow: the tab connects and shows the picture.
- Deny: the tab ends with the refusal reason (e.g. "The user at the device refused."); agent audit 2101 / 2100 on the device.

#### UI-AD-06 · Show them a code
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** `normaluser` signed in on the device.

**Steps:**
1. FlaUI: connect with "Show them a code – they read it out to you".
2. Task in `normaluser`'s session: read the 6-digit code (UIA).
3. Type it into the "6-digit code" box, click *Connect*.
4. Reconnect; type a wrong code three times.

**Expected:**
- Right code: connected.
- Wrong code: "<n> attempt(s) left." counts down; after the third the connection is refused.

#### UI-AD-07 · View-only
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Notepad open on the device.

**Steps:**
1. FlaUI: tick "View only – no mouse and keyboard" on the connect page; connect.
2. Click into the tab and type text.
3. Copy text on the admin's PC; try Ctrl+V on the device.
4. Task on the device: read Notepad and the clipboard.

**Expected:**
- Badge "View only" in the header.
- No text reaches Notepad; nothing reaches the device's clipboard (clipboard off).

Note: view-only is chosen on the connect page before connecting; there is no toggle while connected.

#### UI-AD-08 · Use RDP instead
P2 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop tab open (connect page or connected).

**Steps:**
1. FlaUI: click *Use RDP instead*.
2. Check the connection's tabs.

**Expected:**
- An RDP tab "Remote desktop" opens to the same device.

**Copy and paste**

#### UI-CP-01 · Text admin → device (agent)
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop connected; Notepad open on both sides.

**Steps:**
1. In Notepad on the admin's PC type a multi-line Unicode text (`Zeile 1 äöü\r\nLine 2 ✓ 日本`), select all, Ctrl+C.
2. Click into the tab, click Notepad on the device, Ctrl+V.
3. Task on the device: read Notepad's text.

**Expected:**
- Same text on the device, line breaks and Unicode intact.

#### UI-CP-02 · Text device → admin (agent)
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** As UI-CP-01.

**Steps:**
1. Through the tab: in Notepad on the device type the test text, select all, Ctrl+C.
2. Click Notepad on the admin's PC, Ctrl+V.
3. Read Notepad on the admin's PC.

**Expected:**
- Same text on the admin's PC.

#### UI-CP-03 · Image both ways (agent)
P2 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop connected; Paint on both sides.

**Steps:**
1. Copy a 200×200 test image in Paint on the admin's PC; paste in Paint on the device through the tab.
2. Task on the device: read the clipboard image and compare pixels.
3. Copy a test image on the device; paste in Paint on the admin's PC; compare.

**Expected:**
- Pixels equal both ways.
- Notices "Copied the image from the device – paste it here." / "Pasted the image from the device." as applicable.

#### UI-CP-04 · Files admin → device (agent)
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** `C:\lab\clip` with 3 files and a folder with files (a few MB) on the admin's PC.

**Steps:**
1. In Explorer on the admin's PC select the 3 files and the folder, Ctrl+C.
2. Limit the admin's PC's network adapter to 20 Mbit/s (`Set-VMNetworkAdapter -MaximumBandwidth`; Hyper-V limits only what a VM sends, so the limit belongs on the side that sends the files) – otherwise the 20 MB are across before the progress can be seen.
3. Through the tab: in Explorer on the device open a folder, Ctrl+V.
4. Watch the tab for the transfer progress; remove the limit.
5. PS Direct on the device: compute SHA-256 of all arrived files.

**Expected:**
- Progress "Copying <what> to the device" shows in the tab, then "Copied <what> to the device – paste it there." / done.
- All files and the folder arrive; hashes equal the source.

#### UI-CP-05 · Files device → admin (agent)
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Test files on the device.

**Steps:**
1. Through the tab: copy the files in Explorer on the device.
2. Paste in Explorer on the admin's PC.
3. Compute SHA-256 on both sides.

**Expected:**
- Files arrive; hashes equal; notice "Copied <n> files from the device – paste them here." before pasting.

#### UI-CP-06 · Too much
P2 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** A 101 MB file on the admin's PC.

**Steps:**
1. Copy the file in Explorer on the admin's PC.
2. Through the tab: paste in Explorer on the device.
3. Read the tab's notice; list the target folder on the device.

**Expected:**
- Notice: "Too much to copy through the remote desktop (at most 100 MB and 1000 files) – use the file transfer instead."
- No file (and no partial file) on the device.

#### UI-CP-07 · Policy text only
P2 · How: U S PS · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** PS Direct on the device: `HKLM\SOFTWARE\Policies\RemoteHub\Agent` DWORD `DesktopClipboard` = 1.

**Steps:**
1. FlaUI: connect the agent desktop.
2. Copy text on the admin's PC and paste on the device.
3. Copy a file on the admin's PC and paste on the device.
4. Read the tab's notice.

**Expected:**
- Text arrives.
- Files are refused: "Copying images and files isn't allowed on this device – only text."

#### UI-CP-08 · Copy on the lock screen
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop connected to `normaluser`'s session.

**Steps:**
1. Lock the device session (`Lock-LabSession`), Send Ctrl+Alt+Del for the password field.
2. Copy text on the admin's PC; click into the picture (the keyboard focus leaves the toolbar's button) and try Ctrl+V into the password field through the tab.
3. Task on the device: check that the text didn't reach the clipboard; copy text in the locked session and check it doesn't reach the admin's PC.
4. Unlock through the tab; copy text again and paste on the device.

**Expected:**
- While locked: nothing reaches the device's clipboard and nothing comes back. The agent's service closes the clipboard both ways while the lock, sign-in or Ctrl+Alt+Del screen is up (LogonUI runs in the session); a file or image copy offered then is refused with "Nothing is copied while the device shows its lock or sign-in screen – copy again once it is unlocked."
- After unlock: copy and paste works again.

#### UI-CP-09 · Text both ways (RDP)
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RDP tab connected, clipboard redirection on.

**Steps:**
1. As UI-CP-01 through the RDP tab.
2. As UI-CP-02 through the RDP tab.

**Expected:**
- Same text both ways (Unicode, multi-line).

#### UI-CP-10 · Files both ways (RDP)
P2 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RDP tab connected, clipboard redirection on.

**Steps:**
1. Copy 3 files in Explorer on the admin's PC; paste in Explorer in the RDP session.
2. Copy files in the RDP session; paste on the admin's PC.
3. Compare SHA-256.

**Expected:**
- Files arrive both ways; hashes equal.

#### UI-CP-11 · Image both ways (RDP)
P3 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RDP tab connected.

**Steps:**
1. Copy a test image on the admin's PC; paste in Paint in the RDP session.
2. Copy a test image in the session; paste on the admin's PC.
3. Compare pixels.

The automated test puts the image on the clipboard as a 24-bit DIB (as Paint copies one) and reads the pixels from the
DIB on the other side, honouring its header. Put there with `Clipboard.SetImage` and read with `GetImage`, it arrived
3 pixels to the right both ways – the colour masks of a 32-bit DIB read as pixels somewhere in Windows' RDP clipboard,
not in RemoteHub.

**Expected:**
- Pixels equal both ways.

#### UI-CP-12 · Across sessions
P3 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RDP tab to device A and agent desktop tab to device B (both connected).

**Steps:**
1. In the RDP tab copy a text in Notepad on device A.
2. Switch to the agent tab; paste in Notepad on device B.
3. Task on device B: read the text.
4. When it didn't arrive: whether the text is on the admin's PC's clipboard (read by another program) and RemoteHub's log lines about the agent tab's clipboard ("text copied on this PC sent …" or "this PC's clipboard couldn't be read …"), so the evidence tells which half failed.

**Expected:**
- The text from device A arrives on device B.

**Ctrl+Alt+Del**

#### UI-SAS-01 · Agent tab, desktop
P1 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop connected to an unlocked session.

**Steps:**
1. FlaUI: click "Send Ctrl+Alt+Del" in the tab.
2. Screenshot the tab.
3. Press Esc through the tab; screenshot again.

**Expected:**
- The security options screen (Lock, Switch user, Sign out, Task Manager) shows in the tab.
- After Esc the desktop is back.

Note: Windows frames the first option ("Lock") with the keyboard focus, and OCR may not read framed text – in lab run 20261019-193627 it read every option but "Lock" although `ui-desktop-sas-options-tab.png` shows it. When "Lock" isn't read, the test moves the focus down once (Down arrow through the tab) and reads the picture again (`ui-desktop-sas-options-moved-tab.png`).

#### UI-SAS-02 · Agent tab, **lock screen**
P1 · How: U S C · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop connected to `normaluser`'s session as `PSAdmin`.

**Steps:**
1. Lock the device session.
2. FlaUI: click "Send Ctrl+Alt+Del"; screenshot the tab.
3. Type `normaluser`'s password through the tab, Enter.
4. Check the tab and the device: no new consent dialog, no "Waiting for … to allow access", the tab live.
5. Repeat while connected as `normaluser` itself (own account).

**Expected:**
- The password field shows in the tab; typing unlocks the session.
- `normaluser` allowed this connection before the lock, so after the unlock it continues without asking – the user who allowed it isn't asked again (agent-desktop-draft.md §4.2 step 4, `HoldUserDesktop` in `DesktopEndpoint.cs`). Someone else unlocking or signing in is asked first (D-LOCK-02, UI-SAS-03). As `normaluser` (own account) it continues as well.

Note: until lab run 20261016-212514 the test expected `normaluser` to be asked again; the agent did as designed (no dialog, the tab stayed live), so the expectation was corrected.

#### UI-SAS-03 · Agent tab, sign-in screen
P1 · How: U C · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Nobody signed in on the device (sign-in screen).

**Steps:**
1. FlaUI: open the agent desktop, pick the console, connect.
2. Click "Send Ctrl+Alt+Del"; screenshot.
3. Click into the picture (the keyboard focus stays on the toolbar's button otherwise), type `PSAdmin` and the lab password through the tab, Enter.
4. Wait for the desktop.

**Expected:**
- The sign-in screen shows in the tab; Ctrl+Alt+Del opens the credential fields.
- `PSAdmin` signs in; the tab shows the desktop (own account – nobody asked).

Note: in lab run 20261019-193627 the keys were typed right after the button click and went to RemoteHub's toolbar – Tab moved to the privacy screen button and Enter opened its menu (`ui-desktop-signin-desktop-client.png`); the device's sign-in screen timed out back to the clock. The test now clicks into the picture first.

#### UI-SAS-04 · Agent tab, UAC
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop connected to `normaluser`'s session; `SecureDesktop` right for `PSAdmin` (default for Administrators).

**Steps:**
1. Task in the user's session: `Start-Process cmd -Verb RunAs`.
2. Screenshot the tab.
3. Type the lab password into the UAC prompt through the tab, Enter. `normaluser` isn't an administrator: the prompt offers the device's administrators with the first one (the console account, `Admin`) chosen and its password field focused – there is no user name field to type `PSAdmin` into (lab run 20261016-212514 typed `PSAdmin`, Tab, password into that field and nothing was elevated).
4. PS Direct: look for an elevated `cmd.exe` in that session; close a prompt still open (`consent.exe`), so the session doesn't stay on the secure desktop for the next cases.

**Expected:**
- The UAC prompt is visible in the tab.
- After the password an elevated `cmd.exe` runs as the console account.

#### UI-SAS-05 · Physical Ctrl+Alt+Del stays local
P2 · How: U C · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop tab has keyboard focus.

**Steps:**
1. Console (C): send Ctrl+Alt+Del to the client VM (`Send-LabCtrlAltDel`).
2. Screenshot the client VM console and the device console.
3. Press Esc on the client.

**Expected:**
- The client VM shows its own security screen.
- The device shows nothing new (no security screen).

#### UI-SAS-06 · RDP tab, desktop
P1 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RDP tab connected.

**Steps:**
1. FlaUI: click "Send Ctrl+Alt+Del to the session".
2. Screenshot the tab; Esc.

**Expected:**
- The RDP session shows its security screen in the tab.

#### UI-SAS-07 · RDP tab, **lock screen**
P1 · How: U S · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RDP tab connected as `PSAdmin`.

**Steps:**
1. Lock the RDP session (task in that session: `rundll32 user32.dll,LockWorkStation`).
2. FlaUI: click "Send Ctrl+Alt+Del to the session".
3. Type the lab password through the tab, Enter.
4. Task in the session: check it's unlocked.

**Expected:**
- The password field shows in the tab; typing unlocks the RDP session.

**Tools and network**

The context menu's *Tools* group lists *PowerShell*, *File transfer*, *Registry*, *Services* and *Processes* only once the connection's test has passed the way it connects now (WinRM or the agent). Until then it has one entry instead: *Test PowerShell remoting* or *Test RemoteHub Agent* ("… again" after a failure, "Testing …" and switched off while it runs). So each case below runs that test from the menu first when the tools aren't offered, and waits for "… works (…)" before it opens a tool.

#### UI-TL-01 · PowerShell tab
P1 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** Connection works over the agent; a second connection works over WinRM HTTPS ("Use HTTPS" on).

**Steps:**
1. FlaUI: right-click → *Tools* → *Test PowerShell remoting* (*Test RemoteHub Agent* for the agent connection); wait for "… works (…)", click *OK*. Then right-click → *Tools* → *PowerShell*; type `Get-Service Spooler`, Enter; read the output.
2. Type `Get-Ser` and press Tab; read the completion list.
3. Type `exit`, Enter.
4. Repeat for the WinRM HTTPS connection.

**Expected:**
- Output shows the Spooler service.
- A completion list opens above the input line (e.g. `Get-Service`).
- `exit` closes the tab. Same over WinRM HTTPS.

#### UI-TL-02 · File transfer tab
P1 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** `C:\lab\up` with a folder of files on the admin's PC.

**Steps:**
1. FlaUI: right-click → *Tools* → *File transfer*; type `C:\Temp` in the remote address bar, Enter.
2. Drag the folder from the local side to the remote side.
3. Wait for the File transfers pane to show done.
4. PS Direct on the device: compute SHA-256 of the files; check for `*.remotehub-part`.
5. Download the folder back to another local folder; compare hashes.

**Expected:**
- All files arrive with equal hashes; no `.remotehub-part` file left.
- The download back matches too.

#### UI-TL-03 · Registry tab
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** PS Direct created `HKLM\SOFTWARE\RemoteHubLab` on the device.

**Steps:**
1. FlaUI: right-click → *Tools* → *Registry*; type `HKLM\SOFTWARE\RemoteHubLab` in the address bar, Enter.
2. Right-click → *New* → string value `LabValue` = `one`.
3. Edit it to `two`; PS Direct: `reg query HKLM\SOFTWARE\RemoteHubLab /v LabValue`.
4. Delete it (confirm); `reg query` again.

**Expected:**
- `reg query` shows `two` after step 3.
- After delete `reg query` finds no `LabValue`.

#### UI-TL-04 · Services tab
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** Spooler running on the device.

**Steps:**
1. FlaUI: right-click → *Tools* → *Services*; filter `Spooler`.
2. Click *Stop*, confirm.
3. PS Direct: `(Get-Service Spooler).Status`.
4. Click *Start*; check again.

**Expected:**
- Status `Stopped` after step 2, `Running` after step 4; the tab shows the same.

#### UI-TL-05 · Processes tab
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** `notepad.exe` running on the device.

**Steps:**
1. FlaUI: right-click → *Tools* → *Processes*; filter `notepad`.
2. Select it, *End process* (Del), confirm.
3. PS Direct: `Get-Process notepad`.
4. Filter `lsass`, select it, press Del.

**Expected:**
- Notepad is gone on the device.
- lsass: "lsass.exe is a critical Windows process and can't be ended from here."; lsass still runs.

#### UI-TL-06 · Set up PowerShell remoting
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** WinRM off on the device; connection with "Use HTTPS" off.

**Steps:**
1. FlaUI: right-click → *Tools* → *Test PowerShell remoting*; in the result click *Set up on the target…*. (The later UI-AGENT cases run this setup themselves when this case didn't run before them, e.g. in a rerun of single cases.)
2. Choose "HTTPS with a new certificate for this computer …"; click *Save as .ps1…* to `C:\lab\setup.ps1`.
3. Copy the script to the device and run it elevated via PS Direct (`Invoke-LabSetupScript`).
4. Click *Test again*; compare the thumbprint and confirm the trust.

**Expected:**
- The script runs without errors and prints a thumbprint.
- After *Test again* the connection switches to HTTPS and shows "PowerShell remoting works (…)."

#### UI-NET-01 · Agent tab reconnect
P1 · How: U N · Needs: 2VM · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Preconditions:** Agent desktop connected to `normaluser`'s session as `PSAdmin` (user allowed it).

**Steps:**
1. Cut the device's network adapter for 10 s (`Disconnect-VMNetworkAdapter`), then restore it.
2. Watch the tab: overlay "Connection lost – reconnecting…".
3. Measure the time until the picture is live again.
4. Check the device for a new consent window.

**Expected:**
- The tab reconnects within 30 s of the cut.
- No new consent window; agent audit 1103 on the device.

#### UI-NET-02 · RDP auto-reconnect
P2 · How: U N · Needs: 2VM · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Preconditions:** RDP tab connected.

**Steps:**
1. Cut the device's network adapter for 10 s, then restore it.
2. Read the tab status.
3. Wait up to 60 s.

**Expected:**
- Status "Connection lost – reconnecting (<n>/<max>)…", then "Connected" in the same session (same session ID on the device).

### 10.6 Recordings

#### C-REC-01 · Record each tab type
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `C-REC.Tests.ps1`

**Preconditions:** Connection `Device` with RDP and the agent available; recording notice not yet accepted.

**Steps:**
1. FlaUI: open an RDP tab; click "Record this tab to a video file"; accept "Record this tab" → *Start recording*.
2. Wait 10 s; click the red *REC 00:10* button to stop; choose *Close*.
3. Repeat for agent desktop, PowerShell, File transfer, Registry, Services and Processes tabs. Before the agent desktop tab the person at the device is warmed up (its first call starts its server and loads the OCR – not against the consent dialog's 30 s countdown); then it clicks *Allow*.
4. PS Direct: list `C:\Users\Admin\Videos\RemoteHub`.
5. Read each file's duration with Media Foundation.

**Expected:**
- One MP4 per tab, named `Device - <tab> - <yyyy-MM-dd HH-mm-ss>.mp4` with `<tab>` = Remote desktop, Desktop (agent), PowerShell, File transfer, Registry, Services, Processes.
- Each file has duration > 0 and plays.
- Audit "Recording started" / "Recording saved" per tab.

Note: the agent desktop tab is called "Desktop (agent)" in the file name.

#### C-REC-02 · Settings
P3 · How: U PS · Needs: 2VM · Status: ✅ Automated – `C-REC.Tests.ps1`

**Preconditions:** A connected tab.

**Steps:**
1. FlaUI: *Settings → Recordings*: frame rate "30 fps – smooth, for presentations", size "Full HD – 1920 × 1080", "Capture with the graphics card" on.
2. Record a tab for 10 s.
3. Read the file's width, height and frame rate (Media Foundation).

**Expected:**
- 1920 × 1080, 30 fps.

#### C-REC-03 · GPU capture
P3 · How: U · Needs: 2VM · Status: ✅ Automated – `C-REC.Tests.ps1`

**Preconditions:** GPU capture on (C-REC-02); one Win11 and one Win10 client VM.

**Steps:**
1. Record a tab for 10 s on Win11; take console screenshots while recording.
2. Same on Win10.
3. Check both files play; check the diagnostic log for "GPU capture failed, using GDI".

**Expected:**
- Win11: no yellow border on the screenshots.
- Win10: recording works (a yellow border may show).
- Both files play.

#### C-REC-04 · Unexpected stop
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `C-REC.Tests.ps1`

**Preconditions:** A connected tab.

**Steps:**
1. FlaUI: start recording, wait 20 s.
2. PS Direct: `Stop-Process -Name RemoteHub -Force`.
3. Read the file's duration with Media Foundation; play it.

**Expected:**
- The MP4 opens and plays; duration > 0 (fragmented MP4).

#### C-REC-05 · Stop time
P3 · How: U PS · Needs: 2VM · Status: ✅ Automated – `C-REC.Tests.ps1`

**Preconditions:** A PowerShell tab showing a clock (`while(1){Get-Date; sleep 1}`).

**Steps:**
1. FlaUI: record; note the time of the stop click.
2. Read the file's duration and the last frame (clock on screen).

**Expected:**
- Duration ≤ the time between start and stop click; the last frame doesn't show a time after the stop.

#### C-REC-06 · N edition
P3 · How: U · Needs: N · Status: ✅ Automated – `C-REC.Tests.ps1`

**Preconditions:** Client VM `W11-26H2-N-en-US` without Media Feature Pack.

**Steps:**
1. FlaUI: open a tab, click Record, accept the notice.
2. Read the message.
3. Check RemoteHub still runs; check the folder for a broken file.

**Expected:**
- "Recording stopped": "The recording of <connection> · <tab> stopped: The H.264 video encoder is not available on this PC (Windows N editions need the Media Feature Pack) …"
- No crash; RemoteHub keeps running.

### 10.7 Network shares

These cases exist as `lab\tests\S-SHARE.Tests.ps1` but are not in the sheet yet.

Note: the `S-` prefix clashes with section 6 (S = agent sign-in and transport security). Suggested new prefix **F-**:
S-SMB-01..04 → F-SMB-01..04, S-SFTP-01..03 → F-SFTP-01..03, S-FTP-01..02 → F-FTP-01..02, S-DAV-01..03 → F-DAV-01..03
(the Pester `It` names and `-Tag`s have to change with it). P is not set in the test file; the values below are proposed.

How the tests run:
- Only on Windows Server VMs; client VMs skip the file.
- `Reset-LabVm`, then `Enable-LabShareServers` sets up via PS Direct:
  - SMB shares `rhshare` and `rhshare2` (`C:\rhshare`, `C:\rhshare2`; access for `PSAdmin` and `normaluser`);
  - OpenSSH server on port 2222;
  - IIS FTP site `RHFtp` (port 21, TLS required, data ports 50000–50100);
  - WebDAV at `/dav` on the Default Web Site (Windows authentication, HTTPS on 443);
  - one self-signed certificate for FTP and WebDAV.
- `Invoke-LabShare` runs the harness `share` / `share-conflict` command (`ShareHarness.cs`) **on the host**, with RemoteHub's own `ShareConnector` and `ShareTransfer`. RemoteHub's window isn't involved. The password is the lab password unless `-WrongPassword` is given.
- The `share` command:
  - connects (a key or certificate seen for the first time is accepted only with `--accept 1`);
  - lists the start folder, creates `rh-lab-<id>`;
  - uploads `pack\big.bin` (3 MB + 17 bytes) and `pack\sub\small.txt` ("RemoteHub lab ä ö ü");
  - checks the size and that no `.remotehub-part` is left;
  - renames to `big-renamed.bin`, downloads and compares SHA-256 and the text;
  - deletes the folder again.
- Results are saved as `s-share-*.json` in the evidence folder.

#### S-SMB-01 · Opens a share through Windows and copies both ways
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** Share servers set up; no Windows connection to the VM from the host yet.

**Steps:**
1. Harness: `share --protocol smb --host <ip> --path rhshare --user PSAdmin --password <lab password>`.
2. The harness runs the full step list (connect, list, new folder, upload, size, no part files, rename, expand, download, same content, delete).
3. Save the result as `s-share-smb.json`.

**Expected:**
- `ok` = true (every step ok).
- `description` starts with "SMB · Windows" (Windows' own SMB client).
- `start` = `\\<ip>\rhshare`.

#### S-SMB-02 · Uses RemoteHub's own client for a second account on the same server
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** Share servers set up.

**Steps:**
1. Harness: `share-conflict --host <ip> --share1 rhshare --user1 PSAdmin --share2 rhshare2 --user2 normaluser --password <lab password>`.
2. The harness opens `rhshare` as `PSAdmin` and `rhshare2` as `normaluser` at the same time and lists both.
3. With the second connection it writes `conflict-<id>.txt` ("second account"), reads its size and deletes it.
4. Save the result as `s-share-smb-conflict.json`.

**Expected:**
- `first` starts with "SMB · Windows".
- `second` contains "RemoteHub client" ("SMB · RemoteHub client (Windows already uses another account for this server)"): Windows error 1219 made RemoteHub fall back to its own SMB client.
- `writtenBySecond` = 14 (bytes).

#### S-SMB-03 · The standard user copies both ways on the share it may use
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** Share servers set up (`normaluser` has Modify on `C:\rhshare2`).

**Steps:**
1. Harness: `share --protocol smb --host <ip> --path rhshare2 --user normaluser --password <lab password>`.
2. The harness runs the full step list.
3. Save the result as `s-share-smb-user.json`.

**Expected:**
- `ok` = true (every step ok).

#### S-SMB-04 · A wrong password is reported as such
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** Share servers set up.

**Steps:**
1. Harness: `share --protocol smb --host <ip> --path rhshare --user PSAdmin --password not-the-password`.
2. Read the `connect` step.

**Expected:**
- `ok` = false.
- The `connect` step's error contains "password" (Windows' message, e.g. "\\<ip>\rhshare: The user name or password is incorrect.").

#### S-SFTP-01 · Asks about the host key once and copies both ways
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** OpenSSH server on port 2222; no trusted key stored.

**Steps:**
1. Harness: `share --protocol sftp --host <ip> --port 2222 --user PSAdmin --password <lab password> --accept 1`.
2. The harness answers the host key question with yes and runs the full step list.
3. Save the result as `s-share-sftp.json`; keep `accepted` for S-SFTP-02.

**Expected:**
- `ok` = true.
- Exactly one question (`asked`, "SSH host key").
- `accepted` starts with `SHA256:`.
- `description` starts with "SFTP" ("SFTP · <server version>").

#### S-SFTP-02 · Connects without asking once the key is accepted
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** S-SFTP-01 passed (its `accepted` fingerprint).

**Steps:**
1. Harness: `share --protocol sftp --host <ip> --port 2222 --user PSAdmin --password <lab password> --trusted <accepted> --accept 0`.
2. The harness runs the full step list.

**Expected:**
- `ok` = true.
- No question asked (`asked` is empty).

#### S-SFTP-03 · Refuses a key that differs from the accepted one when the user says no
P1 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** OpenSSH server on port 2222.

**Steps:**
1. Harness: `share --protocol sftp --host <ip> --port 2222 --user PSAdmin --password <lab password> --trusted SHA256:not-this-key --accept 0`.
2. The harness answers the key question with no.
3. Read `asked` and the `connect` step.

**Expected:**
- `ok` = false.
- The first question's `What` contains "changed" ("SSH host key (changed!)").
- The `connect` error contains "wasn't accepted" ("The server's key wasn't accepted – nothing was sent.").

#### S-FTP-01 · Asks about the self-signed certificate once and copies both ways over TLS
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** IIS FTP site `RHFtp` with TLS required; the certificate's SHA-256 known from `Enable-LabShareServers`.

**Steps:**
1. Harness: `share --protocol ftp --host <ip> --user PSAdmin --password <lab password> --ftp-security explicit --accept 1`.
2. The harness accepts the "certificate Windows doesn't trust" question and runs the full step list.
3. Save the result as `s-share-ftps.json`.

**Expected:**
- `ok` = true.
- `accepted` = the server certificate's SHA-256 (`AB:CD:…` form).
- `description` starts with "FTPS".

#### S-FTP-02 · Sends nothing when the certificate is refused
P1 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** IIS FTP site `RHFtp` with TLS required.

**Steps:**
1. Harness: `share --protocol ftp --host <ip> --user PSAdmin --password <lab password> --accept 0` (FTP security defaults to explicit TLS).
2. The harness answers the certificate question with no.
3. Read the `connect` step.

**Expected:**
- `ok` = false.
- The `connect` error contains "certificate" ("The server's certificate wasn't accepted – nothing was sent.").

#### S-DAV-01 · Copies both ways over HTTPS with Windows authentication
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** WebDAV at `/dav` with HTTPS (self-signed certificate) and Windows authentication.

**Steps:**
1. Harness: `share --protocol webdav --host <ip> --path /dav --user PSAdmin --password <lab password> --accept 1` (HTTPS is the default).
2. The harness accepts the certificate and runs the full step list.
3. Save the result as `s-share-webdav-https.json`.

**Expected:**
- `ok` = true.
- `accepted` = the server certificate's SHA-256.

#### S-DAV-02 · Copies both ways over HTTP
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** WebDAV at `/dav` on port 80.

**Steps:**
1. Harness: `share --protocol webdav --host <ip> --path /dav --user PSAdmin --password <lab password> --https 0`.
2. The harness runs the full step list.
3. Save the result as `s-share-webdav-http.json`.

**Expected:**
- `ok` = true (description "WebDAV · HTTP, not encrypted").

#### S-DAV-03 · A wrong password is reported as such
P2 · How: H PS · Needs: Server VM · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** WebDAV at `/dav` on port 80.

**Steps:**
1. Harness: `share --protocol webdav --host <ip> --path /dav --user PSAdmin --password not-the-password --https 0`.
2. Read the `connect` step.

**Expected:**
- `ok` = false.
- The `connect` error contains "password" ("The user name or password is incorrect.").

## 11. Cases added or changed since v2 (from the sheet)

Generated on 2026-10-15 from `test-lab-cases.md` for every case that has no section above yet: the expected result is the sheet's, the steps are in the test named in the status (or still to be written). Checked against RemoteHub / RemoteHub Agent 0.18.11.

### 5.1 First install

#### A-INST-27 · Folder made before the install
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-MORE2.Tests.ps1`

**Expected:**
- normaluser creates `%ProgramData%\RemoteHub\Agent\logs\x.log` (and a subfolder) before the install → afterwards every entry owned by Administrators, only inherited ACEs (SYSTEM, Administrators), normaluser can neither read nor write

#### A-INST-28 · `AUTOSIGNIN=1` on a package without the provider
P3 · How: PS H · Needs: – · Status: ✅ Automated – `A-MORE2.Tests.ps1`

**Expected:**
- install succeeds without DLL and CLSID keys; Hello `autoSignIn` false; automatic sign-in not allowed – neither in `agent.json` (`noUserBehavior`, `autoSignInGroups`) nor in the policy key. Today the install's choice is still recorded (`agent.json` since 0.18.12, the policy key before; "Automatic sign-in allowed on this device for Administrators.") – product gap: `auto-sign-in=[AUTOSIGNIN]` outside the `CredentialProviderDll` condition

### 5.2 Other install paths

#### A-HAND-07 · Provider added or removed on an installed agent
P2 · How: PS · Needs: – · Status: ✅ Automated – `A-MORE2.Tests.ps1`

**Expected:**
- `msiexec /i <msi> ADDLOCAL=CredentialProvider` / `/i {ProductCode} REMOVE=CredentialProvider` → 0/3010, DLL and both keys added / removed; same service PID, agent.json unchanged (its `noUserBehavior` = AutoSignIn and `autoSignInGroups` from the `AUTOSIGNIN=1` install stay – only the enroll step and RemoteHub's `auto-sign-in on/off` set them), no new enroll line; the policy key is not touched (empty: the install doesn't write it since 0.18.12)

### 5.4 Uninstall

#### A-UNINST-15 · Uninstall after automatic sign-in
P1 · How: PS · Needs: – · Status: ✅ Automated – `A-UNINST.Tests.ps1`

**Expected:**
- after an `AUTOSIGNIN=1` install (`agent.json` `noUserBehavior` = AutoSignIn, `autoSignInGroups` with Administrators; policy key empty, no `autosignin.policy` – docs/admx-design.md §11.3): provider DLL and both keys gone, `agent.json` and with it the install's choice removed, the policy key still empty
- Steps: see the test (`lab/tests/A-UNINST.Tests.ps1`).

#### A-UNINST-16 · The install's choice, IT's policy values and an older version's values
P3 · How: PS · Needs: – · Status: ✅ Automated – `A-MORE2.Tests.ps1`

**Expected:**
- after an `AUTOSIGNIN=1` install (its choice in `agent.json`, policy key empty) IT sets `Rights\AutoSignIn` → uninstall removes `agent.json`, IT's value stays
- IT sets `NoUserBehavior=SignInScreen` → `AUTOSIGNIN=0` (update through the agent) writes `noUserBehavior` = `SignInScreen` and clears `autoSignInGroups` in `agent.json` (not set would mean allowed – automatic sign-in is on by default), IT's value stays
- migration (planted, so it doesn't depend on the previous release): the policy key and `autosignin.policy` as a version before 0.18.12 leaves them – `NoUserBehavior` unchanged, `Rights\AutoSignIn` changed by IT – then a service restart → `NoUserBehavior` moved to `agent.json`, `Rights\AutoSignIn` stays in the policy key, the marker deleted, the log says "moved NoUserBehavior" and "Rights\AutoSignIn changed since the install – left in the policy key"

### 5.5 Configuration and service behaviour

#### S-THR-06 · What counts toward the block
P2 · How: H E · Needs: – · Status: ✅ Automated – `A-MORE2.Tests.ps1`

**Expected:**
- 5 refused sign-ins with the right password from one address (normaluser 403, a deny-network-logon account) block it, the 6th gets 429; unmapped Windows errors (HTTP 500) don't count

#### S-THR-07 · Shared sign-in slots
P3 · How: H · Needs: – · Status: ✅ Automated – `A-MORE2.Tests.ps1`

**Expected:**
- at most 8 Windows sign-ins at once across all addresses; no slot within 30 s → 503 (needs ≥ 2 source addresses)

#### P-SES-10 · PowerShell can't start
P3 · How: H PS E · Needs: – · Status: ✅ Automated – `A-MORE2.Tests.ps1`

**Expected:**
- e.g. an IFEO `Debugger` on powershell.exe pointing to a missing file → 500, log "Could not start PowerShell for <acct>: …", no event 1000, the session slot released

### 7.1 Network shares of a connection (S-SHARE)

#### S-SMB-05 · No account → Windows account
P3 · How: H · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- SMB without credentials connects as the signed-in Windows user; description "SMB · Windows · your Windows account", tab source "your Windows account (no account set)"

#### S-SMB-06 · Windows connection lifetime
P2 · How: H PS · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- no drive letter; two tabs on one share → one connection; closing the last tab removes it (`net use` empty); a connection that existed before is used and stays; RemoteHub exiting removes its own

#### S-SMB-07 · Port / name rules
P3 · How: H U · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- port ≠ 445 → "SMB always uses port 445 – remove the port from the share."; empty share name → "The share has no share name – edit it and enter the name …"; a pasted `\\host\share\folder` fills in host and path in the editor

#### S-SMB-08 · Access denied for a local admin
P3 · How: H PS · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- a share allowing only Administrators, opened as a local admin → Windows' message plus "… A local administrator signing in over the network gets no administrator rights there (Windows' remote UAC) …"

#### S-SFTP-04 · SFTP needs an account
P2 · How: U H · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- no account → "<name> (SFTP) needs an account. Right-click it → Edit… and set one …"; editor "SFTP needs a user name."; without a key, password and keyboard-interactive only (a key-only server refuses it like a wrong password; keys: S-SSH-04, S-SSH-05)

#### S-SFTP-05 · Wrong password
P2 · How: H · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- "The user name or password is incorrect (or the server doesn't allow password sign-in)."

#### S-FTP-03 · FTP without TLS
P3 · How: H U · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- security None → description "FTP · not encrypted", address `ftp://`, the editor warns "Not encrypted: the password and every file go over the network in plain text …"

#### S-FTP-04 · Implicit TLS / anonymous
P3 · How: H · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- implicit → port 990; no account → user "anonymous", tab source "anonymous sign-in (no account set)"; wrong password → "The user name or password is incorrect."

#### S-DAV-04 · No account / redirect / status codes
P3 · How: H · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- no account → the Windows credentials; 301/302/307/308 → "The server redirects to <Location> – use that address for the share."; 403/404/409/423/507 → their messages

#### S-SHR-01 · Password not stored
P2 · How: U · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- prompt "No password is stored for <user>. Enter it to open <server › share> – it is kept in memory only."; Reconnect reuses it without asking; nothing written to the stored secrets

#### S-SHR-02 · Trust dialog / Forget
P2 · How: U · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- first key or certificate → "Trust this server?" (*Trust and connect*); a changed key → "Server key changed"; the accepted value is kept; *Forget* in the editor → asked again next time
- the question has no time limit: answered 30 s after it appears (a user comparing the fingerprint), *Trust and connect* still connects and *Cancel* on a changed key gives "The server's key wasn't accepted – nothing was sent."

#### S-SHR-03 · Idle close
P3 · How: H U · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- after 5 min idle "Idle – the connection to the share was closed and opens again when you use it."; the next listing reconnects without asking

#### S-SHR-04 · Transfer safety
P2 · How: H · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- uploads go to `<name>.remotehub-part`, size checked, then renamed; existing file without overwrite → "Skipped – the file already exists"; same-name folder → "Skipped – a folder has that name"; size mismatch → "… the transfer was incomplete." and the part file removed

#### S-SHR-05 · Browse actions
P3 · How: H U · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- deleting the share root → "The share itself can't be deleted."; renaming onto an existing name → "\"<n>\" already exists."

#### S-SHR-06 · Shares on their own
P2 · How: U · Needs: Server · Status: ✅ Automated – `S-SHARE2.Tests.ps1`

**Expected:**
- New → New connection → SMB: host required ("Enter the host the share is on (name or address)."), inherits the folder's account, Duplicate / Move under in its menu; Remove: "Nothing on the share itself is deleted." and its open tabs closed

#### W-NAME-01 · Setup by computer name
P3 · How: PS · Needs: – · Status: ✅ Automated – `A-MORE2.Tests.ps1`

**Expected:**
- certificate CN=<name>, SAN `DNS=<fqdn>` and `DNS=<short>`; reuse needs the same CN; a name with other characters refused by RemoteHub "… isn't a computer name or IP address."

### 7.1a SSH connections and private keys (S-SSH)

#### S-SSH-01 · Terminal over SSH
P1 · How: H · Needs: Server · Status: ◐ Test written – `S-SSH.Tests.ps1`

**Expected:**
- password sign-in on port 2222; "Trust this server?" asked once (SSH host key) and the fingerprint kept; the tab's status "Connected – SSH · OpenSSH…"; `hostname` typed into the terminal prints the device's name; `exit` → "The session ended."

#### S-SSH-02 · Host key
P1 · How: H · Needs: Server · Status: ◐ Test written – `S-SSH.Tests.ps1`

**Expected:**
- an accepted key → no question; refused → "The server's key wasn't accepted – nothing was sent."; a different key than the accepted one → asked as "SSH host key (changed!)" and, refused, the same message

#### S-SSH-03 · Terminal size
P2 · How: H · Needs: Server · Status: ◐ Test written – `S-SSH.Tests.ps1`

**Expected:**
- the shell gets the tab's size; after a resize to 100×30 `mode con` shows 100 columns

#### S-SSH-04 · Private key sign-in
P1 · How: H · Needs: Server · Status: ◐ Test written – `S-SSH.Tests.ps1`

**Expected:**
- an ed25519 key with a passphrase in `administrators_authorized_keys` signs in without a password; a wrong passphrase → "The passphrase is wrong …"; none → "… protected by a passphrase …"; a key the server doesn't know → "The server didn't accept the private key for this user (is its public key in the user's authorized_keys?)"; that key and the password → signs in (the key first, then the password)

#### S-SSH-05 · SFTP with a private key
P2 · How: H PS · Needs: Server · Status: ◐ Test written – `S-SSH.Tests.ps1`

**Expected:**
- SFTP signs in with the key alone and copies both ways; the host key SSH accepted is the SFTP share's too (no question)

#### S-SSH-06 · SSH in RemoteHub's window
P2 · How: U · Needs: Server · Status: ◐ Test written – `S-SSH.Tests.ps1`

**Expected:**
- New → New connection → SSH opens "New SSH connection" (no start folder, *Import key…* in the account); *Add*, *Open* → "Trust this server?" → *Trust and connect*; `hostname` typed into the terminal shows the device's name (read through the terminal's UI Automation value); Ctrl+W goes to the shell (the tab stays); `exit` → "The session ended."

#### S-SSH-07 · Nothing identifying in DiagLog
P1 · How: H E · Needs: Server · Status: ◐ Test written – `S-SSH.Tests.ps1`

**Expected:**
- after the SSH sessions this PC's `remotehub.log` has "SSH: connected" and names neither the device, its address nor the account

### 9.1 Connect, Hello, sessions

#### D-CON-11 · Unrequested sign-in request
P3 · How: H E · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- a SignInRequest (type 31) RemoteHub wasn't asked for closes the connection; during a session it is a protocol error and the resume token is void

### 9.2 Consent

#### D-ASK-08 · RemoteHub closes while asking
P3 · How: H E · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- 2100 "…: The connection ended before access was granted."

#### D-ASK-09 · Hide apps from the question
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- *Allow and hide apps…* opens the app picker; the countdown restarts; the chosen apps hidden from the start

#### D-PIN-06 · PIN dialog choices
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- the code window has *Hide apps…* and *Deny*; Deny → Denied

#### D-NONE-05 · Own session with Ask / Pin
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- the own session started with Ask or Pin → the dialog is shown all the same

#### D-MODES-03 · No mode left
P2 · How: PS H E · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- `ConsentModes` allowing nothing usable → Hello consentModes [], Failed "No way to connect is allowed on this device (policy).", 2100

#### D-MODES-04 · Mode on a locked / sign-in session
P3 · How: PS H · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- a mode the policy doesn't allow isn't refused there: it runs as None, the follow-up mode applies when someone unlocks or signs in

### 9.3 Banner and user at the device

#### D-BAN-08 · Never collapsed while hiding
P3 · How: S C · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- something hidden → the banner stays open with " · mouse and keyboard blocked" and 'Click "Disconnect" to end' – on the lab's 1024 × 768 screen the banner is taller and wraps them rather than cutting them

#### D-BAN-09 · Clicks pass through
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- the admin's clicks on the banner's area reach the window below (`WS_EX_TRANSPARENT`)

#### D-BAN-10 · No banner after automatic sign-in
P1 · How: H C · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- a session automatic sign-in created shows only the cover, no banner

#### D-BAN-11 · Click-through kept against programmatic cursor moves
P2 · How: H S C · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Preconditions:** normaluser signed in on the console and allows PSAdmin; the marker window (`Start-LabMarker`) full screen below the banner; the cursor mover (`RhPerson.ps1 -Do cursor-mover`) started in normaluser's session, waiting.

**Steps:**
1. Harness client connects (Allow), waits 12 s for the pill, and clicks the banner's centre – the admin's pointer now rests on the banner.
2. The cursor mover starts: in turns, `SetCursorPos` to exactly the admin's spot and a relative `SendInput` move of 3 px (synthetic moves, as a full-screen RDP / Hyper-V client makes), reading the banner's `WS_EX_TRANSPARENT` at each step; it runs 5 s.
3. While it keeps moving, the harness clicks the same spot again.
4. The mover stops; the banner is read 20 more times.
5. The console mouse (`Send-LabMouse`, real input) moves to 120 px below the banner.

**Expected:**
- The mover's `SendInput` moves reached a low-level hook, all flagged `LLMHF_INJECTED`.
- While the cursor was moved by program, every reading of the banner is `WS_EX_TRANSPARENT` – before the fix of #29 the first synthetic move made it clickable.
- The marker records the second click (`mdown` within 4 px); the session doesn't end.
- After the moves stopped the banner is still click-through; within 15 s of the console mouse's move it is clickable again (the device's own mouse takes it back).

### 9.5 Secure desktop, sign-in, lock, user switch

#### D-SIGNIN-05 · Own account by SID
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- the account signing in at the device is recognised as the admin's own by its SID (not the name) → continued without asking

### 9.6 Ctrl+Alt+Del

#### D-SAS-12 · Screen hidden by the user
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- Ctrl+Alt+Del while the user hides the screen → "The user at the device has hidden their screen – Ctrl+Alt+Del isn't sent until they show it again."

### 9.7 Input

#### D-IN-08 · No input into hidden apps
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- mouse down / wheel over a hidden app and key-down while it has the focus are dropped

Note: the person at the device first clicks the marker with the console mouse, and the test waits for that click to arrive, so the marker has the keyboard focus (the consent dialog takes the foreground, as in D-INPUT); the admin's own click would be dropped in the hidden run. In run 20261016-212514 the control run saw nothing at all: the previous context had left its session parked with the app picker open in the middle of the screen, where the clicks and keys went. Each context now waits until no agent window is left before it starts.

#### D-IN-09 · Input stops when the user hides
P2 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE.Tests.ps1`

**Expected:**
- the user hides the screen → held keys released, no input passes

Note: "Hide screen" is clicked on the banner by its whole label, found in the console picture (the banner is kept out of screen captures). Clicking the word "Hide" took "Hide apps…" in runs 20261016-184035 and -212514, so the screen was never hidden, the admin's Ctrl+Alt+Del got through and the read of the banner then failed on the secure desktop ("Capture … The handle is invalid"). The test waits for event 1105 before the admin's input goes out.

### 9.8 Picture, encoder, resolution, monitors

#### D-VID-11 · RDP windows keep drawing
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- while connected `RemoteDesktop_SuppressWhenMinimized` = 2, restored afterwards (`rdp-minimized-<sid>.txt`), log line

Note: the helper sets the value for the session's user (`HKEY_USERS\<sid>`, any session, not only RDP ones) as soon as it starts – already while the consent dialog is up – and puts it back when it ends (`KeepRdpDrawing.cs`, `DesktopHelper.cs:103`). A refused connection therefore has it back at 0 before the test reads it; the test first checks that the connection was allowed. Lab run 20261016-212514 (W11 lane 2): the person's first click came after the 30 s countdown (its server was still starting), the connection was refused and D-VID-11 read 0.

#### D-VID-12 · GPU picture stands still
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- Desktop Duplication frozen while the screen changes → GDI for the rest of the connection, log "… using GDI for this connection"

#### D-VID-13 · Display kept on
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- the display doesn't turn off while connected

#### D-VID-14 · Frame rate on a slow link
P2 · How: H N · Needs: – · Status: ✅ Automated – `D-VIDEO.Tests.ps1`

**Preconditions:** the D-VID-05 session: the VM's adapter limited to 2 Mbit/s, a window of random pixels changing all the time, 30 fps and quality 80 asked.

**Steps:**
1. Read every `Stats` of the 30 s session: quality and `frameRateLimit`.

**Expected:**
- at least one stat has a `frameRateLimit` above 0 – the device lowered the frame rate
- every stat with a limit has quality 50 or less: the quality reached the readable level before the frame rate went down
- every limit is between 5 and 29 fps

#### D-FIT-04 · Fit bounds
P3 · How: H · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- below 320×200 ignored, capped at 7680×4320

#### D-VD-05 · Off after a service restart
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Preconditions:** installed with the virtual display; agent.json `"virtualDisplay": "always"`, service restarted.

**Steps:**
1. Connect; send Settings with Fit 1280×720 (switches the virtual monitor on).
2. PS Direct: device state; kill the service process, start it again.
3. PS Direct: device state 10 s after the agent answers again.

**Expected:**
- While connected the virtual display device is on (status OK); agent log "virtual monitor on at 1280×720".
- After the service started again it is switched off (disabled), although the connection was still open when the service was killed.

Note: the device is disabled by design (ConfigManagerErrorCode 22) except while a connection needs it. In every lab run so far (W11 and Server 2025) it couldn't be switched on – see the finding for D-VD-05 above.

### 9.9 Clipboard

#### D-CLIP-19 · Newer copy cancels the first
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- a second copy to the device while the first is still offered replaces it silently

#### D-CLIP-20 · Long text
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- text over 1,000,000 characters truncated silently

#### D-CLIP-21 · Too many files
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- 1001 single files → only 1000 offered, no message

#### D-CLIP-22 · Bad image
P3 · How: H S · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- a broken BMP to the device: receipt ok, the device's clipboard unchanged

#### D-DROP-01 · Files dropped on the tab
P2 · How: H PS E · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Preconditions:** normaluser signed in at the console and allowing the connection (the D-CLIP setup); the host's test files `file1.txt` and `folder\sub\inner.txt`.

**Steps:**
1. The harness drops `file1.txt` and `folder` on the tab (`dropFiles`, as RemoteHub's drag and drop sends them).
2. Read `C:\Users\normaluser\Desktop\From RemoteHub` (files, hashes, owner) and the Application log's events 1108 since the start.

**Expected:**
- the receipt is ok and names the folder "From RemoteHub"
- `file1.txt` and `folder\sub\inner.txt` are there with the host's SHA-256
- the owner of `file1.txt` is normaluser – written with the user's rights, not the agent's
- an event 1108 says "saved 2 file(s)" and names no file

#### D-DROP-02 · Same names again
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Steps:**
1. In the same connection, drop `file1.txt` again.

**Expected:**
- the receipt names a folder `From RemoteHub\<yyyy-MM-dd HH.mm.ss>`
- the first `file1.txt` is still there, unchanged; the second is in the dated folder

#### D-DROP-03 · Junction in the folder's place
P1 · How: H PS · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Steps:**
1. As normaluser, move `From RemoteHub` aside and create a junction with that name to `C:\Windows\System32\drivers\etc`.
2. Drop `file2.txt` on the tab.

**Expected:**
- the junction is in place (control)
- the drop fails – written as normaluser, it can't go through the junction
- no `file2.txt` in `drivers\etc`

#### D-DROP-04 · Policy
P2 · How: H PS · Needs: – · Status: ✅ Automated – `D-CLIP.Tests.ps1`

**Steps:**
1. Set the agent policy `DesktopClipboard` = 2 (images), connect, drop `file3.txt`.

**Expected:**
- refused with "Copying files isn't allowed on this device."

### 9.10 Idle, reconnect, robustness

#### D-IDLE-04 · Clipboard counts as activity
P3 · How: PS H · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- clipboard and transfers reset the idle time

#### D-RES-08 · Token void after an invalid message
P3 · How: H · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- a protocol error ends the session and its resume token

#### D-RES-09 · No token after a service restart
P3 · How: H PS · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- a resume after a service restart → consent again

#### D-RES-10 · The agent notices the drop first
P1 · How: H N PS E · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- network cut ~75 s (beyond the 30 s keep-alive), resumed within 1 min → 1101 "(connection lost …)", 1103, no new question. Fails today: the end is taken as "closed by RemoteHub" and the token revoked (`DesktopEndpoint.cs:220-230`)

#### D-ROB-07 · Unrequested sign-in request
P3 · How: H E · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- "invalid message from RemoteHub: A sign-in request the agent didn't ask for." / "Sign-in request too large."

#### D-ROB-08 · Message over 16 MiB
P3 · How: H E · Needs: – · Status: ✅ Automated – `D-MORE2.Tests.ps1`

**Expected:**
- 1101 "(invalid message from RemoteHub: Message too large.)"

### 9.11a Privacy screen, hidden apps, user privacy

#### D-PRIV-01 · Privacy screen
P1 · How: PS H C E · Needs: – · Status: ✅ Automated – `D-PRIVACY.Tests.ps1`

**Expected:**
- right PrivacyScreen, policy `PrivacyScreen` 0 / 1 / 2 → Hello `privacyScreen` / `privacyInputBlock` and feature `privacyScreen` match; turned on → console screenshot black while frames still arrive, event 1104; refused → 2103
- Steps: see the test (`lab/tests/D-PRIVACY.Tests.ps1`).

#### D-PRIV-02 · Local input blocked
P1 · How: H S C · Needs: – · Status: ✅ Automated – `D-PRIVACY.Tests.ps1`

**Expected:**
- level 2: the device's own keyboard and mouse don't reach the session (marker app); level 1: they do, and a request with blocking is refused with 2103 "Blocking the mouse and keyboard at the device isn't allowed on this device (policy)."
- Steps: see the test (`lab/tests/D-PRIVACY.Tests.ps1`).

#### D-PRIV-03 · Not available before 2004
P2 · How: H E · Needs: 1809 · Status: ✅ Automated – `D-PRIVACY.Tests.ps1`

**Expected:**
- on W10-1809 no `privacyScreen` feature, a request refused with 2103
- Steps: see the test (`lab/tests/D-PRIVACY.Tests.ps1`).

#### D-PRIV-04 · Kept after a resume
P2 · How: H N · Needs: – · Status: ✅ Automated – `D-PRIVACY.Tests.ps1`

**Expected:**
- privacy screen still on after D-RES-01
- Steps: see the test (`lab/tests/D-PRIVACY.Tests.ps1`).

#### D-PRIV-05 · Snips and switching blocked
P1 · How: H S C · Needs: – · Status: ✅ Automated – `D-PRIVACY2.Tests.ps1`

**Expected:**
- while any privacy screen is up Print Screen, Win+Shift+S, Alt+Tab and Win+Tab are dropped at the device and from the admin; Win+S and Shift+Tab work; switching works again when it is off

#### D-PRIV-06 · Snipping tools closed
P2 · How: H S PS · Needs: – · Status: ✅ Automated – `D-PRIVACY2.Tests.ps1`

**Expected:**
- SnippingTool / ScreenClippingHost / ScreenSketch closed when they show a window; agent log "… was closed – a snip would show the screen at the device", no event

#### D-PRIV-07 · View-only has none
P3 · How: H · Needs: – · Status: ✅ Automated – `D-PRIVACY2.Tests.ps1`

**Expected:**
- a privacy screen request on a view-only connection is ignored

#### D-PRIV-08 · Monitors change
P3 · How: H PS E · Needs: – · Status: ✅ Automated – `D-PRIVACY2.Tests.ps1`

**Expected:**
- monitors change while it is on → overlays rebuilt, or 2103 "… turned off because the device's monitors changed"

#### D-HIDE-01 · Hidden apps by policy
P1 · How: PS H S · Needs: – · Status: ✅ Automated – `D-PRIVACY.Tests.ps1`

**Expected:**
- `HiddenApps` (full path and `aumid:`) → those windows never in the frames, whatever the user chooses
- Steps: Notepad opens a file with a secret text in normaluser's session; the test waits (60 s) for its window, restores it if minimised and brings it to the front (an Alt tap and `SetForegroundWindow` in the session, a console click into it if that wasn't enough), keeps a console screenshot (`d-priv-apps-console.png`), then takes a control frame without the policy (`visible.png`), sets `HiddenApps` to the program of the process that owns the window and takes a frame with it (`hidden.png`). The window's title, rectangle, foreground state and program are in `d-priv-apps.json`.
- Precondition: the secret text is in the control frame – otherwise the case fails with "precondition: the app's window wasn't in the visible picture" and the hiding wasn't tested. The text counts as read when the OCR has a word at most 3 characters off it (Server 2025 read "BHLABSECRETAPPTEXT" in run 20261016-212514); the same rule decides that it leaked into `hidden.png`.

#### D-HIDE-02 · The user hides
P1 · How: H S E · Needs: – · Status: ✅ Automated – `D-PRIVACY.Tests.ps1`

**Expected:**
- the user at the device hides the screen or chosen apps ("Allow and hide apps…") → event 1105 and a Privacy message; kept after a resume without a new question; cleared when another user signs in
- Steps: see the test (`lab/tests/D-PRIVACY.Tests.ps1`).

#### D-HIDE-03 · Policy apps aren't the user's
P3 · How: PS H S E · Needs: – · Status: ✅ Automated – `D-PRIVACY2.Tests.ps1`

**Expected:**
- today policy `HiddenApps` count as user-hidden: the first Privacy message has hiddenApps=1, the banner "1 app hidden", 1105 "the user … hides 1 app(s)" – product finding

### 9.11b Shell surfaces over the privacy screen (spike, not merged)

#### D-SHELL-01 · Helper token UIAccess switch
P2 · How: PS H · Needs: – · Status: ✅ Automated – `D-SHELL.Tests.ps1`

**Expected:**
- helper token TokenUIAccess 1 by default, 0 with `HelperUIAccess`=0

#### D-SHELL-02 · Shell surfaces stay covered
P1 · How: H C S · Needs: – · Status: ✅ Automated – `D-SHELL.Tests.ps1`

**Expected:**
- with UIAccess and `PrivacyScreen`=2: Start, Search, Quick Settings, Notification Center, a toast, a taskbar thumbnail → the console below the banner strip < 0.5 % bright (Alt+Tab / Task View are dropped by SnipGuard, D-PRIV-05)

#### D-SHELL-03 · Overlays still excluded from capture
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-SHELL.Tests.ps1`

**Expected:**
- overlays keep `WDA_EXCLUDEFROMCAPTURE` (0x11) with UIAccess

#### D-SHELL-04 · Banner above the overlays
P2 · How: S · Needs: – · Status: ✅ Automated – `D-SHELL.Tests.ps1`

**Expected:**
- the banner's z-order above the overlays

#### D-SHELL-05 · Disconnect still works
P1 · How: H S · Needs: – · Status: ✅ Automated – `D-SHELL.Tests.ps1`

**Expected:**
- the device's user ends the session with *Disconnect* through the input block → Ended "The user at the device ended the session."

### 9.12 Automatic sign-in – credential provider (C-PROV)

#### C-PROV-01 · Install with `AUTOSIGNIN=1`
P1 · How: PS · Needs: – · Status: ✅ Automated – `C-PROV.Tests.ps1`

**Expected:**
- msiexec exit 0/3010; `RemoteHubCredentialProvider.dll` in Program Files, signature valid (lab certificate trusted on the VM); both keys for CLSID `{718FFFD2-…}`, `ThreadingModel` Apartment
- Steps: see the test (`lab/tests/C-PROV.Tests.ps1`).

#### C-PROV-02 · Inert without a ticket
P1 · How: C PS · Needs: – · Status: ✅ Automated – `C-PROV.Tests.ps1`

**Expected:**
- with the provider registered and no sign-in pending, signing out and in again with Ctrl+Alt+Del and the password reaches the desktop
- Steps: see the test (`lab/tests/C-PROV.Tests.ps1`).

#### C-PROV-03 · Sign-in through the provider
P1 · How: C PS · Needs: – · Status: ✅ Automated – `C-PROV.Tests.ps1`

**Expected:**
- Lab DLL + `RemoteHubCpTestServer.exe` (SYSTEM): client LogonUI accepted, `Result` status 0, the console account's desktop, the ticket file deleted
- Steps: see the test (`lab/tests/C-PROV.Tests.ps1`).

#### C-PROV-04 · Update keeps it
P1 · How: PS · Needs: – · Status: ✅ Automated – `C-PROV.Tests.ps1`

**Expected:**
- a rebuild of the same version (new ProductCode, a major upgrade) without `AUTOSIGNIN` → DLL and keys still there
- Steps: see the test (`lab/tests/C-PROV.Tests.ps1`).

#### C-PROV-05 · `AUTOSIGNIN=0` removes it
P1 · How: PS · Needs: – · Status: ✅ Automated – `C-PROV.Tests.ps1`

**Expected:**
- DLL and both keys gone; `agent.json` `noUserBehavior` = SignInScreen (turned off – not set would mean on) and `autoSignInGroups` cleared, the policy key stays empty
- Steps: see the test (`lab/tests/C-PROV.Tests.ps1`).

#### C-PROV-06 · RemoteHub's *Install credential provider*
P1 · How: H PS · Needs: – · Status: ✅ Automated – `C-PROV.Tests.ps1`

**Expected:**
- `AgentInstaller.ChangeCredentialProvider` over the agent → installed, DLL signed, both keys; `agent.json` `noUserBehavior` = AutoSignIn and `autoSignInGroups` = S-1-5-32-544, the policy key not written (since 0.18.12); message "Automatic sign-in allowed on this device"
- Steps: see the test (`lab/tests/C-PROV.Tests.ps1`).

#### C-PROV-07 · RemoteHub's *Remove credential provider*
P1 · How: H PS · Needs: – · Status: ✅ Automated – `C-PROV.Tests.ps1`

**Expected:**
- removed, DLL and keys gone, `agent.json` `noUserBehavior` = SignInScreen again, the policy key still empty; the agent's service process is the same as before both actions and health answers with the same certificate
- Steps: see the test (`lab/tests/C-PROV.Tests.ps1`).

#### C-PROV-08 · Another build on the device
P1 · How: H PS · Needs: – · Status: ✅ Automated – `C-PROV.Tests.ps1`

**Expected:**
- install refused with "a different build … Update the agent first", nothing installed
- Steps: see the test (`lab/tests/C-PROV.Tests.ps1`).

#### C-PROV-09 · No agent / restart needed
P3 · How: H PS · Needs: – · Status: ✅ Automated – `A-MORE.Tests.ps1`

**Expected:**
- `ChangeCredentialProvider` without an installed agent → "isn't installed"; a 3010 exit → the "after the device restarts" message
- Steps: see the test (`lab/tests/A-MORE.Tests.ps1`).

#### C-AUTO-01 · Turned off by IT
P1 · How: H · Needs: – · Status: ✅ Automated – `C-AUTO.Tests.ps1`

**Expected:**
- installed with `AUTOSIGNIN=1` (`agent.json` `noUserBehavior` = AutoSignIn, `autoSignInGroups` = Administrators; policy key and marker not written – since 0.18.12), then `NoUserBehavior=SignInScreen` set as IT would → `Rights.AutoSignIn` false; a request → SignInFailed "turned off". The value deleted again ("Not configured") → the install's choice applies again (the rest of C-AUTO signs in on it)
- Steps: see the test (`lab/tests/C-AUTO.Tests.ps1`).

#### C-AUTO-02 · View-only
P1 · How: H · Needs: – · Status: ✅ Automated – `C-AUTO.Tests.ps1`

**Expected:**
- policy and right set, view-only start → SignInFailed, Windows never asked
- Steps: see the test (`lab/tests/C-AUTO.Tests.ps1`).

#### C-AUTO-03 · No password sent
P2 · How: H · Needs: – · Status: ✅ Automated – `C-AUTO.Tests.ps1`

**Expected:**
- SignInReady, nothing sent for 30 s → SignInFailed "didn't send the sign-in in time"
- Steps: see the test (`lab/tests/C-AUTO.Tests.ps1`).

#### C-AUTO-04 · Wrong password
P1 · How: H PS E · Needs: – · Status: ✅ Automated – `C-AUTO.Tests.ps1`

**Expected:**
- SigningIn → SignInFailed "password was wrong"; no desktop; event 2105
- Steps: see the test (`lab/tests/C-AUTO.Tests.ps1`).

#### C-AUTO-05 · Right password
P1 · How: H C PS E · Needs: – · Status: ✅ Automated – `C-AUTO.Tests.ps1`

**Expected:**
- after closing the failed attempt's message: SignInReady → SignedIn; the console account's desktop; event 1106, no 3101/3102
- Steps: see the test (`lab/tests/C-AUTO.Tests.ps1`).

#### C-AUTO-06 · No password in logs
P1 · How: PS E · Needs: – · Status: ✅ Automated – `C-AUTO.Tests.ps1`

**Expected:**
- the lab password in neither the agent's log files nor the Application log
- Steps: see the test (`lab/tests/C-AUTO.Tests.ps1`).

#### C-END-01 · Covered from the start
P1 · How: H C PS · Needs: – · Status: ✅ Automated – `C-END.Tests.ps1`

**Expected:**
- after SignedIn: a screenshot of the console shows the privacy screen message; keys typed at the console don't reach the desktop; RemoteHub's picture shows the desktop
- Steps: see the test (`lab/tests/C-END.Tests.ps1`).

#### C-END-02 · Lock on disconnect
P1 · How: H PS E · Needs: – · Status: ✅ Automated – `C-END.Tests.ps1`

**Expected:**
- connection closed → the session is disconnected from the console (`WTSDisconnectSession`, on client and Server alike), its programs keep running; event 1107 "… locked (closed by RemoteHub)"
- Steps: see the test (`lab/tests/C-END.Tests.ps1`).

#### C-END-03 · Sign out on disconnect
P1 · How: H PS E · Needs: – · Status: ✅ Automated – `C-END.Tests.ps1`

**Expected:**
- end action SignOut (connection or policy) → the session is gone; event 1107 "signed out"
- Steps: see the test (`lab/tests/C-END.Tests.ps1`).

#### C-END-04 · Policy wins
P2 · How: H PS · Needs: – · Status: ✅ Automated – `C-END.Tests.ps1`

**Expected:**
- policy `AutoSignInEndAction = Lock`, RemoteHub sends SignOut → locked
- Steps: see the test (`lab/tests/C-END.Tests.ps1`).

#### C-END-05 · Network drop and resume
P1 · How: H N C · Needs: – · Status: ✅ Automated – `C-END.Tests.ps1`

**Expected:**
- the VM's adapter cut for 30 s → the overlay stays, the desktop is untouched; reconnected within 2 min → same session, no sign-out
- Steps: see the test (`lab/tests/C-END.Tests.ps1`).

#### C-END-06 · Network drop without resume
P1 · How: H N PS E · Needs: – · Status: ✅ Automated – `C-END.Tests.ps1`

**Expected:**
- adapter cut for 3 min → signed out after 2 min; event 1107. Fails today when the agent notices the cut first (D-RES-10): in lab run 20261016-212514 the keep-alive ended the connection after 27 s as "closed by RemoteHub" and the session was locked at once
- Steps: see the test (`lab/tests/C-END.Tests.ps1`).

#### C-END-07 · Agent restart
P1 · How: H PS E · Needs: – · Status: ✅ Automated – `C-END.Tests.ps1`

**Expected:**
- service killed while connected → at the next start the session gets its end action (signed out when it was dropped). The "dropped" half fails today when the agent takes the harness' drop for a close (D-RES-10)
- Steps: see the test (`lab/tests/C-END.Tests.ps1`).

#### C-END-08 · Cover ends after a drop
P2 · How: H N C · Needs: – · Status: ✅ Automated – `C-END.Tests.ps1`

**Expected:**
- a session automatic sign-in created has no banner (since 0.18.10), only the cover; the cover's helper ending during the 2 minutes → signed out at once, event 1107 "… (the screen cover ended after the connection dropped)". Fails today when the agent takes the harness' drop for a close (D-RES-10)
- Steps: see the test (`lab/tests/C-END.Tests.ps1`).

#### C-END-09 · Input blocked at level 1 too
P1 · How: H C PS · Needs: – · Status: ✅ Automated – `C-END2.Tests.ps1`

**Expected:**
- `PrivacyScreen`=1 → after SignedIn the console's own keys still don't reach the session

#### C-END-10 · No privacy screen available
P2 · How: H C E · Needs: – · Status: ✅ Automated – `C-END2.Tests.ps1`

**Expected:**
- `PrivacyScreen`=0 (`AutoSignInRequirePrivacyScreen` 0) → signed in, 1106 ", no privacy screen on this device", the console shows the desktop, no banner

#### C-END-11 · Ctrl+Alt+Del at the device during the hold
P3 · How: H N C E · Needs: – · Status: ✅ Automated – `C-END2.Tests.ps1`

**Expected:**
- after a drop Ctrl+Alt+Del at the console → the screen offers only Task Manager and Cancel (no Lock – the cover blocks input, privacy-mode-design §5.9); not signed out at once; signed out when the 2 min run out
- Changed after lab run 20261016-212514: the case used to click Lock on that screen, which isn't there while the cover blocks input.

#### C-END-12 · Resume brings the cover back
P2 · How: H N C · Needs: – · Status: ✅ Automated – `C-END2.Tests.ps1`

**Expected:**
- an operator without `Rights\PrivacyScreen` resumes → the service's privacy screen is back. Fails today when the agent takes the harness' drop for a close (D-RES-10)

#### C-END-13 · Failed sign-in removes the cover
P3 · How: H C · Needs: – · Status: ✅ Automated – `C-END2.Tests.ps1`

**Expected:**
- wrong password → the service's privacy screen dropped, a later manual sign-in isn't covered

#### C-END-14 · Who turned the cover on
P3 · How: E · Needs: – · Status: ✅ Automated – `C-END2.Tests.ps1`

**Expected:**
- 1104 for the service's cover reads "the service (automatic sign-in of <admin> from <source>) turned the privacy screen on …", never "<admin> from <source> turned …" – the admin didn't ask for it

#### C-END-15 · The agent notices the drop first
P1 · How: H N PS E · Needs: – · Status: ✅ Automated – `C-END2.Tests.ps1`

**Expected:**
- cut ≥ 75 s, resumed within 2 min → same session, no lock; fails today (D-RES-10)

#### C-AUTO-07 · On without the option, off with `AUTOSIGNIN=0`
P1 · How: H PS · Needs: – · Status: ✅ Automated – `C-AUTO2.Tests.ps1`

**Expected:**
- automatic sign-in is on by default: installed without `AUTOSIGNIN` → provider DLL and keys installed, no `noUserBehavior` / `autoSignInGroups` in `agent.json`, no `NoUserBehavior` policy value, Hello `autoSignIn` true with the `autoSignIn` feature, a request signs the console account in; installed with `AUTOSIGNIN=0` → no provider, `agent.json` `noUserBehavior` = SignInScreen, Hello `autoSignIn` false, no `autoSignIn` feature, a request → "turned off on this device (policy NoUserBehavior)"
- Steps: see the test (`lab/tests/C-AUTO2.Tests.ps1`).

#### C-AUTO-08 · Rights
P1 · How: PS H · Needs: – · Status: ✅ Automated – `C-AUTO2.Tests.ps1`

**Expected:**
- `Rights\AutoSignIn` without the account → "may not sign in automatically on this device (right AutoSignIn)"; without SecureDesktop the Start on the sign-in screen is refused first: Failed "Nobody is signed in or the session is locked – and your account may not see the sign-in screen on this device.", 2100, Hello `autoSignIn` false, log "Desktop: automatic sign-in not offered to <acct>: … needs the rights Desktop and SecureDesktop …"
- Steps: see the test (`lab/tests/C-AUTO2.Tests.ps1`).

#### C-AUTO-09 · Privacy screen required
P2 · How: PS H · Needs: – · Status: ✅ Automated – `C-AUTO2.Tests.ps1`

**Expected:**
- `AutoSignInRequirePrivacyScreen=1` with `PrivacyScreen=0` → refused, the policy named
- Steps: see the test (`lab/tests/C-AUTO2.Tests.ps1`).

#### C-AUTO-10 · Session state
P1 · How: H S · Needs: – · Status: ✅ Automated – `C-AUTO2.Tests.ps1`

**Expected:**
- someone signed in on the console → "Someone is signed in on the console now"; the account has a disconnected session → "already has a session on the device"
- Steps: see the test (`lab/tests/C-AUTO2.Tests.ps1`).

#### C-AUTO-11 · Rate limit
P1 · How: H E · Needs: – · Status: ✅ Automated – `C-AUTO2.Tests.ps1`

**Expected:**
- the 4th automatic sign-in of an account within 15 minutes → "Too many automatic sign-ins", event 2104
- Steps: see the test (`lab/tests/C-AUTO2.Tests.ps1`).

#### C-AUTO-12 · Provider unusable
P1 · How: PS H · Needs: – · Status: ✅ Automated – `C-AUTO2.Tests.ps1`

**Expected:**
- `ExcludedCredentialProviders` lists the CLSID / InprocServer32 outside the agent folder / DLL not signed → Hello `autoSignIn` false, refused with that reason
- Steps: see the test (`lab/tests/C-AUTO2.Tests.ps1`).

#### C-AUTO-13 · IT's policy kept
P1 · How: PS · Needs: – · Status: ✅ Automated – `C-AUTO2.Tests.ps1`

**Expected:**
- `NoUserBehavior=SignInScreen` set before an `AUTOSIGNIN=1` install → left as it is; the install records its own choice in `agent.json` and the log says the policy decides ("… it decides, so automatic sign-in stays off"); still there after the uninstall
- Steps: see the test (`lab/tests/C-AUTO2.Tests.ps1`).

#### C-AUTO-14 · Sign-in not confirmed
P2 · How: H PS E · Needs: – · Status: ✅ Automated – `C-AUTO3.Tests.ps1`

**Expected:**
- the console's new session has another SID → signed out, 3101 "… the console session N has user <sid>, not <acct> … – signed out", Ended "The automatic sign-in couldn't be confirmed, so it was undone …" (needs a provider test build)

#### C-AUTO-15 · Console changed before the ticket
P2 · How: H C E · Needs: – · Status: ✅ Automated – `C-AUTO3.Tests.ps1`

**Expected:**
- someone signs in at the console between SignInReady and the ticket → SignInFailed "Someone is signed in on the console now …", 2104 SessionChanged, no ticket sent

#### C-AUTO-16 · Provider never loads
P2 · How: H PS E · Needs: – · Status: ✅ Automated – `C-AUTO3.Tests.ps1`

**Expected:**
- LogonUI doesn't load the provider → SignInFailed "The device's sign-in screen didn't load the RemoteHub credential provider." (2104 NoProvider); result timeout → "The sign-in screen didn't take the sign-in. …"

#### C-AUTO-17 · Second sign-in at once
P3 · How: H · Needs: – · Status: ✅ Automated – `C-AUTO3.Tests.ps1`

**Expected:**
- two connections ask at the same time → the second gets "Another automatic sign-in is running on this device."

#### C-AUTO-18 · Pipe squatted
P2 · How: PS H E · Needs: – · Status: ✅ Automated – `C-AUTO3.Tests.ps1`

**Expected:**
- `\\.\pipe\RemoteHubAgent.AutoSignIn` created by another process before the service → 3102 "Automatic sign-in unavailable: the pipe … couldn't be created (…)", Hello `autoSignIn` false

#### C-AUTO-19 · Foreign pipe client refused
P2 · How: PS · Needs: – · Status: ✅ Automated – `C-AUTO3.Tests.ps1`

**Expected:**
- a client that isn't LogonUI / SYSTEM / the console connects → log "Automatic sign-in: pipe client refused (<why>)", a later automatic sign-in still works; an RDP session's LogonUI on Server refused ("session N isn't the console")

#### C-AUTO-20 · Windows refusals
P2 · How: H PS E · Needs: – · Status: ✅ Automated – `C-AUTO3.Tests.ps1`

**Expected:**
- account locked / disabled / expired password / "Deny log on locally" → SignInFailed with Windows' reason ("The account is locked." …), 2105 with the status

#### C-AUTO-21 · DLL signed by another certificate
P3 · How: PS H · Needs: – · Status: ✅ Automated – `C-AUTO3.Tests.ps1`

**Expected:**
- trusted, but not signed by the agent's certificate → "… isn't signed by the same certificate as the agent", Hello `autoSignIn` false

### 9.13 Jump hosts (J)

#### J-00 · Isolation
P1 · How: H · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- this PC reaches the jump host; RDP, WinRM and the agent port of the device behind it fail directly
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-01 · RDP through the forwarder
P1 · How: H · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- the device's RDP certificate (CN = computer name) comes back through the tunnel; the full sign-in and RemoteHub's "Trust this RDP certificate?" question are UI-JUMP-01
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-02 · WinRM through the forwarder
P1 · How: H · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- HTTPS to 127.0.0.1 with the name not checked opens a session (HTTP to 127.0.0.1 / localhost needs TrustedHosts on the admin's PC – not changed on the lab host)
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-03 · The agent through the jump host
P1 · How: H PS E · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- health, PowerShell, files both ways, registry, services, processes, the remote desktop (picture decoded); jump host audit 1200 / 1201 with the device's address and port; the device's audit shows the jump host as the source
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-04 · Without the agent
P1 · How: H · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- PowerShell over WinRM through the jump host
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-05 · Policy
P1 · How: PS H E · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- `TunnelEnabled=0` (and no `tunnel` in health), `TunnelTargets` without the device, a port outside `TunnelPorts`, right `Tunnel` removed (PowerShell still works) – each refused with its message: `TunnelEnabled=0` → `/v1/tunnel` 404 before sign-in, no audit; right `Tunnel` removed → 403 at sign-in, audit 2000 "not a member of an allowed group"; TunnelTargets / TunnelPorts → audit 2200 "(TunnelTargets)" / "(TunnelPorts)"; loopback and 169.254.169.254 refused by default, a name never opens them, `127.0.0.0/8` listed does
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-06 · Tunnel drop
P1 · How: H N · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- the jump host's side towards this PC off for 30 s → the desktop resumes without a new question, tools connect again
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-07 · Forwarder only for RemoteHub
P1 · How: H · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- another program on this PC connecting to the forwarder's local port gets nothing; RemoteHub's own connection works
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-08 · Throughput
P2 · How: H · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- 2 GB to the device arrive unchanged while its remote desktop keeps sending pictures
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-09 · Install and update through it
P1 · How: H PS · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- the scope offered is the jump host's address (`LocalAddressAsync`: a probe – the agent only resolves and checks the target, answers Refused `probe` "<host>:<port> is allowed; not connected (probe).", no TCP connection to the device), install over WinRM through the jump host, firewall rule scoped to the jump host, update through the agent through it
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-10 · Shares of a device behind it
P2 · How: H PS · Needs: Jump, Server · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- SMB (always RemoteHub's own client: "SMB · RemoteHub client"), SFTP and WebDAV copy both ways; FTP refused ("FTP can't go through a jump host …", J-22)
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-11 · Moving in the tree
P2 · How: U · Needs: Jump · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Expected:**
- connection / folder moved between jump host, folder under it, another jump host and a plain folder → the route follows; the drops of §4.3 refused with "<target> is reached through <outer>. Only one jump host is supported for now." / "<j> is a jump host itself …" / "<inner> in this folder is a jump host itself …"; *Move under* lists only connections with an agent or children (at most 40); settings inherited past the jump host
- Steps: see the test (`lab/tests/UI-RDP.Tests.ps1`).

#### J-12 · Older RemoteHub
P2 · How: U PS · Needs: Jump · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Expected:**
- a RemoteHub without jump hosts (≤ 0.18.7) opens the new database and shared file, keeps the items below a jump host as ordinary items, and its save keeps them
- Steps: see the test (`lab/tests/UI-RDP.Tests.ps1`).

#### J-13 · Other refusals
P2 · How: PS H · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- a name that doesn't resolve → `dns`; a closed port → `unreachable` / `timeout`; more than `MaxTunnelStreams` → `limit`; no 2200 for these – unreachable only in the agent log "Jump connection <acct> from <src>: <host:port> unreachable (…)"
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-14 · Limits shared with sessions
P3 · How: PS H · Needs: Jump · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Expected:**
- jump connections count toward `maxSessions`; an unreadable policy key → `/v1/tunnel` 404
- Steps: see the test (`lab/tests/J-JUMP.Tests.ps1`).

#### J-15 · Indentation
P3 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- inside a jump host's frame its connections, folders and shares sit 12 px further in than a folder's items

#### J-16 · Route shown
P2 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- tree "via <jump>" beside the address, tooltip "Reached through <jump> (its RemoteHub Agent is the jump host) …"; editor "Reached through <jump> – its RemoteHub Agent is the jump host …"; share tab "<description> · via <jump>"; tooltip location "… › <jump> › <device>"

#### J-17 · Jump host without agent
P2 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- connecting a child → "<c> is reached through <j>, but <j> has no RemoteHub Agent. Install it there …, or move <c> out of <j> to connect directly."; tree tooltip "<j> has no RemoteHub Agent – install it there to reach the items below it."

#### J-18 · Jump host credentials
P2 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- none → "No credentials are set for the jump host <j> …"; no stored password → asked once ("… kept in memory only."), then reused for every child and tab, or taken from an open agent tab of <j>

#### J-19 · Deleting a jump host
P2 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- "Delete connection "<n>"? … <k> item(s) are reached through it …" with *Keep them* (moved next to it, reached directly) or *Delete all*

#### J-20 · Duplicate a jump host
P3 · How: H · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- copy "<n> (copy)" without the items under it; its agent and RDP certificate pin cleared

#### J-21 · Move changes credentials
P2 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- moving into or out of a jump host when the inherited credentials change → "Move – credentials change", target 'connection "<j>" (reached through it)'; the jump host's own credentials are never inherited

#### J-22 · FTP share behind a jump host
P2 · How: U H · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- menu *Open* off, tooltip "Reached through <j>. FTP can't go through a jump host – its data connections go to addresses the server announces. SMB, SFTP and WebDAV can."; opened anyway → the same text

#### J-23 · Share editor host hints
P3 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- a jump host's share on another host → "Reached directly, like <s> itself. …"; a child's share → "Another device, reached through <j> like <s>."

#### J-24 · Drops beside §4.3
P3 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- a share on its own dropped onto a connection lands next to it; *Move under* puts it under a jump host; an .rdp file dropped on a jump host is imported under it, on a child next to that child (under the same jump host)

#### J-25 · Install below a jump host without WinRM
P2 · How: U · Needs: Jump · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- the install dialog defaults to "Save the installer and the enrollment file to a folder…", hints "<c> is reached through <j> … allow connections from <j>'s address …", hides "Use the agent … afterwards"

#### J-26 · Agent refusals of /v1/tunnel
P2 · How: H · Needs: Jump · Status: ✅ Automated – `J-JUMP2.Tests.ps1`

**Expected:**
- 404 → "The RemoteHub Agent on <h:p> can't be used as a jump host: jump connections are turned off by a policy on the device (TunnelEnabled), or the agent there is older …"; 403 → "… may not use … as a jump host … (right Tunnel – by default the administrators)."; both as "Can't use the jump host <j>: …"

#### J-27 · The jump host's own address
P2 · How: PS H E · Needs: Jump · Status: ✅ Automated – `J-JUMP2.Tests.ps1`

**Expected:**
- its own adapter address refused by default, also when its subnet is in TunnelTargets; allowed only by an entry naming that single address; reason `policy` "… is this jump host itself or a special address …", 2200 "(own or special address)"

#### J-28 · Unreadable policy entries
P3 · How: PS H · Needs: Jump · Status: ✅ Automated – `J-JUMP2.Tests.ps1`

**Expected:**
- TunnelTargets / TunnelPorts with only invalid entries (`not a host!`, `0`, `99999`) refuse every Open with `policy`, never "any"

#### J-29 · Bad tunnel traffic
P3 · How: H E · Needs: Jump · Status: ✅ Automated – `J-JUMP2.Tests.ps1`

**Expected:**
- more than 256 KB unacknowledged on a stream resets it; a text or garbled frame or a duplicate stream id ends the tunnel, 1202 "(protocol error: …)"; a target not closing within 60 s after Close is reset

#### J-30 · A name with allowed and disallowed addresses
P3 · How: PS H · Needs: Jump · Status: ✅ Automated – `J-JUMP2.Tests.ps1`

**Expected:**
- one name, two addresses (hosts file), one in TunnelTargets → connects to the allowed one (each address checked on its own; the design said all must be allowed)

### 9.14 RemoteHub Broker (B)

All cases need the lab's broker (lab.json `Broker`, lab.local.json `BrokerToken`); without it they are skipped with that reason. The run makes its own organisation and deletes it afterwards.

#### B-BRK-01 · MSI with the broker's properties
P1 · How: PS E · Needs: Broker · Status: ◐ Written – `B-BRK.Tests.ps1`

**Expected:**
- `msiexec /i … BROKER_URL=<broker> BROKER_SECRET=<device secret> BROKER_ONLY=1 /qn` on a device without an enrollment file ends with 0 or 3010 and leaves no inbound firewall rule
- the device enrolls itself, registers with the device secret's id and shows online at the broker with its computer name and a fingerprint; agent.json names the organisation
- the Application log has 1303 (registered) and 1300 (connected)

#### B-BRK-02 · Through the broker
P1 · How: H E · Needs: Broker · Status: ◐ Written – `B-BRK.Tests.ps1`

**Expected:**
- RemoteHub's client code connects with the console secret and finds the device's record sealed by the organisation
- the agent's health through a relayed session, pinned to the fingerprint in that record, answers the device's id; the device logs 1301 (session started)

#### B-BRK-03 · Device policy
P1 · How: PS · Needs: Broker · Status: ◐ Written – `B-BRK.Tests.ps1`

**Expected:**
- `BrokerEnabled=0` in the device's policy: the device goes offline at the broker
- the policy cleared: it comes back online

#### B-BRK-05 · Direct on the lab network
P1 · How: H PS E · Needs: Broker · Status: ◐ Written – `B-BRK.Tests.ps1`

**Expected:**
- with the device's Windows Firewall on, the agent's health through the broker answers over the direct path – QUIC on a punched port, over the lab network's own addresses (`DirectLocal`)
- the Application log's 1301 (session started) says "direct local"
- the inbound rule the agent opened for the try ("RemoteHub Agent direct path", that port and the console's address only) is gone within 30 s of the session ending

#### B-BRK-06 · Relayed by device policy
P1 · How: H PS E · Needs: Broker · Status: ◐ Written – `B-BRK.Tests.ps1`

**Expected:**
- `BrokerDirect=0` in the device's policy: the same request answers relayed, and 1301 says "relayed"

#### B-BRK-07 · UDP blocked
P2 · How: H PS E · Needs: Broker · Status: ◐ Written – `B-BRK.Tests.ps1`

**Expected:**
- inbound and outbound UDP of the agent blocked by firewall rules on the device: the request answers relayed within 10 s, without an error

#### B-BRK-04 · Leave and join
P2 · How: PS · Needs: Broker · Status: ◐ Written – `B-BRK.Tests.ps1`

**Expected:**
- `RemoteHubAgent.exe broker leave` says "Left the RemoteHub Broker …", the device goes offline, `broker status` says "No RemoteHub Broker."
- `broker join <url>` with the device secret on standard input – what *Assign to RemoteHub Broker* runs – says "Joined …" and the device comes back online
- the console secret instead is refused ("… console secret …", exit code 1)

#### SEC-BRK-01 · The device secret is kept nowhere
P1 · How: PS E · Needs: Broker · Status: ◐ Written – `B-BRK.Tests.ps1`

**Expected:**
- not in the installer log (`BROKER_SECRET` is hidden), not in agent.json (only the derived enroll and record keys), not in the agent's logs or the Application log, not in `broker status`

### 10.1 Installer

#### C-MSI-09 · Same-version reinstall
P3 · How: PS · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- a rebuilt MSI with the same version replaces the installed one (`AllowSameVersionUpgrades`), exit 0, one Apps entry

### 10.2 Storage, protection, audit

#### C-DB-06 · Unreadable database, no usable backup
P2 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- "RemoteHub's database can't be opened: <reason> Start with empty settings? The unreadable database is kept in <dir> …" (OK / Cancel); OK → `remotehub.db`, `-wal`, `-shm`, `db.key` renamed `.damaged-<stamp>`, `backup\` → `backup.damaged-<stamp>`, a new empty database; Cancel → RemoteHub ends, nothing changed

#### C-DB-07 · Older backup when the newest fails
P3 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- restoring a backup fails → the offer comes again for the next older one

#### C-DB-08 · db.key missing
P3 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- reason "The database key file (db.key) is missing."

#### C-DB-09 · Database of a newer version
P3 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- reason "The database was created by a newer version of RemoteHub (schema N)."

#### C-DB-10 · Daily check finds damage
P2 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- no backup made, the existing ones kept; audit "Database check failed": "The database check found a problem; the existing backups were kept."

#### C-DB-11 · Migration fails
P3 · How: PS · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- the new database deleted, `settings.dat` stays in use; a `settings.dat` that reappears next to a database with data is ignored

#### C-DB-12 · `.migrated` clean-up
P3 · How: PS · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- 30 days after the migration `settings.dat.migrated` and `settings.dat.bak` deleted at start

#### C-DB-13 · Unreadable `settings.dat`
P3 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- a copy `settings.dat.unreadable-<stamp>`; "The settings file could not be read (<err>). A copy was saved to: <path> …"; starts empty

#### C-LOCK-05 · Change the startup password
P2 · How: U PS · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- needs the current one; a new key (an old `lock.json` copy opens nothing), database re-keyed, Hello set up again if it was; "The startup password was changed."

#### C-LOCK-06 · Password rules
P3 · How: U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- "Use at least 8 characters.", "The passwords do not match.", "The current password is wrong.", empty unlock → "Enter the startup password."

#### C-LOCK-07 · Exit while auto-locked
P2 · How: U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- *Exit RemoteHub* ("Disconnects all sessions and closes RemoteHub") ends it without showing the windows again

#### C-LOCK-08 · No auto-lock under a dialog
P3 · How: U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- a modal dialog open → no lock; it locks after the dialog closes

#### C-LOCK-09 · Old RDP Manager folder
P3 · How: U PS · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- with `%APPDATA%\RdpManager` present, turning on offers "Delete the old, unprotected copy of the RDP Manager settings (<path>)"; ticked → the folder deleted

#### C-LOCK-10 · keys.dat sealed
P3 · How: PS · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- with protection on `keys.dat` starts with `RHK2`

#### C-SHARE-04 · Change seen with nothing pending
P2 · How: U · Needs: SMB, 2VM · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- "Settings updated": "The settings file was updated on another device. Load the new version now?" (*Reload*)

#### C-SHARE-05 · Join an existing shared file
P2 · How: U · Needs: SMB · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- "Open shared settings": "This folder already contains a shared settings file. Enter its password … The connections currently on this PC are replaced by the shared ones …" (*Open*)

#### C-SHARE-06 · Back to this PC only
P3 · How: U · Needs: SMB · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- "Use this PC only" … "The shared file itself is not deleted." (*Switch*); audit "Settings location changed" "Settings: this PC"

#### C-SHARE-07 · Forget remembered passwords
P3 · How: U · Needs: SMB · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- `keys.dat` deleted, "Done – the password will be asked again at the next start.", "Unlock shared settings" at the next start

#### C-AUD-05 · Retention in Settings → Logs
P2 · How: U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- choices 30 days … 10 years, default "1 year (default)", "Older entries are removed the next time RemoteHub starts."; audit "Audit log retention changed" "Entries kept for <x> (before: <y>)"; under the policy the box is off, "Set by your organisation."

#### C-AUD-06 · Newest entries removed
P2 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- Verify: "The newest entries were removed: the log ends at entry X, but entry Y was written."; intact → "All N entries are unchanged and complete since they were written."

#### C-AUD-07 · More events
P3 · How: U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- "Added", "Connection failed", "Startup protection changed" ("Startup password" / "… and Windows Hello" / "Off"), "Settings location changed", "Backup exported" ("<n> folders and connections, <m> saved credentials, <p> pages")

#### C-AUD-08 · Bulk save summary
P3 · How: PS U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- more than 50 changes in one save → one "Changed" entry, target "<n> items", detail "<a> added, <c> changed, <d> deleted"

#### C-BAK-04 · Not a backup file
P3 · How: U · Needs: – · Status: ✅ Automated – `C-DATA2.Tests.ps1`

**Expected:**
- "Import failed": "Not a RemoteHub backup file."

### 10.3 Policies, updates, telemetry, crash reports

#### C-POL-08 · `DisableAgentAutoSignIn`
P2 · How: PS U · Needs: – · Status: ✅ Automated – `C-POLUPD.Tests.ps1`

**Expected:**
- the editor rows "Sign in automatically when nobody is signed in" and "When an automatic sign-in's connection ends" hidden; the agent desktop's connect page never offers automatic sign-in, even with the stored option On; the menu entry *Install credential provider* and the install dialog's "Allow automatic sign-in" stay
- Steps: see the test (`lab/tests/C-POLUPD.Tests.ps1`).

#### C-POL-09 · `UpdateChannel` values and types
P3 · How: PS U M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- `ga` / `release` read as stable, `prerelease` / `pre-release` as beta (any case); an unknown value (`nightly`) ignored – the user's channel applies, the channel buttons stay on; a DWORD policy stored as REG_SZ (`DisableTelemetry`="1") ignored

#### C-UPD-06 · No valid checksum
P1 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- `download.sha256` missing, not 64 characters or not hex → "The update has no valid SHA-256 checksum, so it can't be verified."; nothing requested from the download URL

#### C-UPD-07 · Size mismatch
P3 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- more than `download.size` → "The download is larger than announced."; less → "The download is incomplete (x of y bytes)."; neither `.partial` nor `.msi` kept

#### C-UPD-08 · Untrusted redirect
P2 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- an allowed URL redirecting to a host other than github.com / githubusercontent.com / remotehub.dev (and subdomains) → "The download was redirected to an address that isn't trusted."

#### C-UPD-09 · Update server errors
P3 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- *Check for updates now*: 404 on stable → "No release in this channel yet" / "There is no GA (stable) release yet. Switch to the Beta channel…", on beta → "The update server has no release for this channel."; 500 → "Couldn't check for updates"; bad JSON → "…an answer RemoteHub doesn't understand."; no answer in 15 s → "The update server didn't answer in time."; a lower version → "RemoteHub is up to date" (only updates to a higher version)

#### C-UPD-10 · Install declined at the open-sessions question
P3 · How: U M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- "Install cancelled. The verified download is kept until the next update."; no install started – no msiexec other than the Windows Installer service (`msiexec.exe /V`, which the client install before leaves running for a few minutes) and no `install-update.cmd`; *Download and install* enabled again

#### C-TEL-03 · Off discards local records
P2 · How: PS U M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- telemetry off (setting or policy) → at the next start pending `crash\crash-*.json` deleted and never sent, `run-time.json` removed and `install-id` deleted – with the policy as with the setting (C-TEL-02)

#### C-CRASH-03 · Native crash
P3 · How: U E M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- unclean exit with an Application Error 1000 for RemoteHub.exe → `app_crash` with `errorType` `NativeCrash`, message "Exception 0x… in <module> <ver>", stack "<module>+0x<offset>", `version` of the crashed session

#### C-CRASH-04 · Crash queue limits
P3 · How: PS U M · Needs: – · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Expected:**
- at most the newest 5 reports sent; reports older than 30 days deleted; a report the server refuses stays in `crash\` and goes at the next start; a handled "RemoteHub – unexpected error" recorded at most 3 times per session

#### POL-14 · Console features: hidden and refused
P1 · How: U · Needs: – · Status: ✅ Automated – `POL-CLIENT.Tests.ps1`

**Preconditions:** RemoteHub and the RemoteHub Agent on the same client VM, RemoteHub running in the console account's session. WinRM on the VM (`Enable-PSRemoting`, `127.0.0.1` in TrustedHosts). In RemoteHub: *PolLocal* (the VM by its name, the console account) with an agent RemoteHub installed itself through its clipboard install (the lab's own agent removed first) and *Test RemoteHub Agent* passed; *PolWinRm* (`127.0.0.1`, the console account, remoting check on) with *Test PowerShell remoting* passed; *PolRdp* (`192.0.2.10`, with an account); *PolOffline* (`192.0.2.11`, no account). No console policy set; the values below are set while RemoteHub runs.

**Steps:**
1. Without policies: read the menus of PolLocal (the menu, *RemoteHub Agent*, *Tools*), PolWinRm (*Tools*) and PolOffline (the menu, *Tools*), the *New* menu, and Settings → *Security & storage*.
2. Open a PowerShell tab of PolLocal (through the agent) and read the tab bar's *+* menu.
3. HKLM `AllowAgent` = 0 and `AllowAgentDesktop` = 1: read PolLocal's menus and the *+* menu with its *Tools*; open PolLocal's editor → *PowerShell remoting*; open Settings → *Policies*. Remove both, close the tabs.
4. `AllowPowerShellRemoting` = 0: read PolWinRm's menus and PolLocal's *Tools*. Remove it.
5. `AllowAgentFileTransfer` = 0: read the *Tools* of PolLocal and PolWinRm. Remove it.
6. *Connect* on PolRdp; wait until its own attempt has failed. Set `AllowRdp`, `AllowNetworkShares`, `AllowRdpFileImport` and `AllowBackupExport` = 0; *Reconnect* in the toolbar; read the tab and close it. Read PolOffline's menus, the *New* menu and Settings → *Security & storage*. Remove the four.
7. `AllowQuickConnect` = 0: type `pol14-quick.invalid` in the search box and press Enter. Remove it.

**Expected:**
- Without policies: PolLocal offers *Update RemoteHub Agent…* and its tools through the agent, PolWinRm its tools over WinRM, PolOffline *Connect* and *Add network share…*; *New → New connection* has *Remote Control*, *SSH*, *SFTP*, *FTP / FTPS*, *SMB* and *WebDAV*, and *New* has *Import .rdp files…*; Settings has *Export backup…*.
- `AllowAgent` = 0: no *Remote desktop via RemoteHub Agent*, *Install / Update RemoteHub Agent…* or credential provider entry in the tree menu or the *+* menu; *Tools* offers *Test PowerShell remoting*, not *Test RemoteHub Agent* (the tools go over WinRM); in the editor *Connect through* and *Remote desktop through* are greyed out with "Turned off by your organisation"; Settings → Policies marks `AllowAgentDesktop` with *Off because "Allow the RemoteHub Agent" is off*.
- `AllowPowerShellRemoting` = 0: PolWinRm has no *PowerShell*, *File transfer*, *Registry*, *Test PowerShell remoting* or *Set up PowerShell remoting on the target…*; PolLocal still offers *PowerShell* and *File transfer*.
- `AllowAgentFileTransfer` = 0: *File transfer* gone for PolLocal (*PowerShell* stays), still there for PolWinRm.
- The RDP tab's *Reconnect* fails in the tab: "Remote desktop over RDP is turned off by your organisation – see Settings → Policies."; PolOffline has no *Connect* or *Open additional session* and no *Add network share…*; *New → New connection* has none of the share kinds (*SSH* stays) and *New* no *Import .rdp files…*; Settings has no *Export backup…*, *Import backup…* stays.
- Quick connect: "Quick connect is turned off by your organisation – see Settings → Policies."; no tab opens.
- Not checked: the tab's own context menu, the detached window and the toolbar's *File transfer* (they use the same checks as the *+* menu).

#### POL-15 · Settings and connection settings by policy
P1 · How: U · Needs: – · Status: ✅ Automated – `POL-CLIENT.Tests.ps1`

**Preconditions:** as POL-14 (PolLocal paired with the agent on the VM, PolWinRm with the remoting check on, PolOffline, the folder *PolFolder*). No console policy set.

**Steps:**
1. PolOffline's editor → *Local resources & keyboard* → *Local drives* On, Save; *More → Save as .rdp file…* to `C:\lab\pol15-before.rdp`.
2. HKLM `Connections\RedirectDrives` = 0, restart RemoteHub. Read *Local drives* in PolOffline's editor (then Save it unchanged), PolFolder's editor and Settings → *Local resources & keyboard*; save PolOffline as `pol15-with.rdp`.
3. Remove the value, restart RemoteHub; read *Local drives* in PolOffline's editor; save as `pol15-after.rdp`.
4. Settings → *Updates & privacy*: usage data off, Save. `DisableTelemetry` = 0; read the switch.
5. *Install RemoteHub Agent…* for PolWinRm: read the WinRM method, close. `AgentInstall\Methods` = Clipboard, SaveFiles (MULTI_SZ); open it again and read the methods.
6. `AgentDesktopViewOnly` = 1: *Connect* on PolLocal (agent desktop of the VM's own console); read *View only* on the start panel; *Connect*, *Allow* at the console; read the tab.
7. `AllowAgentDesktop` = 0 and `Connections\RemoteDesktopVia` = Agent, restart RemoteHub; read Settings → *Policies*; *Connect* on PolOffline.
8. Settings → General: theme Dark, Save, restart; screenshot. `Theme` = Light, restart; screenshot; read *Theme*.

**Expected:**
- With `RedirectDrives` = 0, *Local drives* shows *Off*, greyed out with "Set by your organisation – see Settings → Policies.", at the connection, the folder and the global defaults; `pol15-with.rdp` has `drivestoredirect:s:` empty, `pol15-before.rdp` and `pol15-after.rdp` `drivestoredirect:s:*`; after the policy is removed the editor shows *On* again (the save with the policy kept the stored value).
- *Send anonymous usage data and crash reports* is on and greyed out, with "Turned on by your organisation".
- Without `Methods` the silent install over PowerShell remoting can be chosen; with it, it is there but greyed out, the clipboard method can still be chosen.
- *View only – no mouse and keyboard* is ticked and greyed out; the session shows the *View only* badge and no *Send Ctrl+Alt+Del*.
- Settings → Policies: "Remote desktop through the RemoteHub Agent is turned off by your organisation, so there is no remote desktop."; *Connect* shows "Remote desktop through the RemoteHub Agent is turned off by your organisation – see Settings → Policies." (title *RemoteHub Agent*) and opens no tab.
- *Theme* shows *Light*, greyed out; the main window is light (mean brightness above 0.5 and at least 0.2 above the run with the user's own Dark).
- Not checked: the RDP session's drives themselves (one VM has no RDP target – the `.rdp` file uses the same resolved settings) and the amber colour of the conflict.

### 10.4 RDP through RemoteHub (2VM)

#### R-RDP-10 · Password toast *Clear now*
P3 · How: U · Needs: 2VM · Status: ✅ Automated – `UI-RDP2.Tests.ps1`

**Expected:**
- 'Stored password for "<name>" copied' / "Press Ctrl+V in the password field, then OK." with *Clear now* (clipboard emptied at once); closes when Windows' dialog closes or the clipboard changes

#### R-RDP-11 · Password prompt "Do nothing"
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP2.Tests.ps1`

**Expected:**
- *When Windows asks for the password* = Do nothing → clipboard untouched, no toast, no "Password copied" audit

#### R-RDP-12 · Switch hotkey inside RDP
P3 · How: U C · Needs: 2VM · Status: ✅ Automated – `UI-RDP2.Tests.ps1`

**Expected:**
- Ctrl+Alt+< → next tab, Ctrl+Alt+Shift+< → previous (full screen left first); on de-DE AltGr+< types the pipe character in the session instead

#### R-RDP-13 · Additional RDP session
P3 · How: U PS · Needs: 2VM · Status: ✅ Automated – `UI-RDP2.Tests.ps1`

**Preconditions:** On the device "Restrict each user to a single session" off – `fSingleSessionPerUser` = 0 both as the setting (`HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server`) and as the policy (`HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services`), Remote Desktop Services restarted. The device is a Windows Server VM (two administrative sessions); a client SKU allows only one session. Also for R-RDP-12 (Device and Device2 are two connections to the same device and account).

**Expected:**
- with an RDP tab open *Open additional session* (tree) / *Additional remote desktop session* (tab) → a second tab and a second session on the device

### 10.5 End to end through RemoteHub's window (2VM)

#### UI-AG-07 · Allow automatic sign-in in the install dialog
P2 · How: U PS · Needs: – · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Expected:**
- checkbox shown only when the package contains the provider, enabled only for remoting and through the agent (shown but off for clipboard / save to folder, nothing passed); starts ticked (on by default) unless RemoteHub's record says the provider was removed; ticked → `AUTOSIGNIN=1`; unticked → `AUTOSIGNIN=0` (the MSI installs the provider by default otherwise)
- Steps: see the test (`lab/tests/UI-DESKTOP.Tests.ps1`).

#### UI-AG-08 · Credential provider from the UI
P2 · How: U PS · Needs: – · Status: ✅ Automated – `UI-DESKTOP.Tests.ps1`

**Expected:**
- right-click → *RemoteHub Agent* → *Install credential provider* (only with the agent installed and the provider in RemoteHub's package) → confirmed, "The credential provider is installed and registered.", audit `agent.credential_provider` "Installed – credential provider"; the entry then reads *Remove credential provider*; with "Sign in automatically when nobody is signed in" On the sign-in screen is signed in, audit `agent.auto_sign_in` "Requested" then "Signed in automatically on the console"
- Steps: see the test (`lab/tests/UI-DESKTOP.Tests.ps1`).

#### UI-AG-09 · Connect page offers automatic sign-in
P2 · How: U S · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- checkbox "Sign in automatically as <account>" only for the console session at the sign-in screen, when the device reports `autoSignIn` and `Rights.AutoSignIn` and `DisableAgentAutoSignIn` isn't set; ticked when the connection option is On; *View only* switches it off; hidden for any other session

#### UI-AG-10 · Sign out asks first
P2 · How: U S · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- end action *Sign out*: the notice after SignedIn ends "When you disconnect, the session is signed out."; Disconnect, closing the tab (x, middle click, Ctrl+W) or the group asks "Sign out on the device?" (*Disconnect* / Cancel keeps it) – the tab's own x shows only with more than one tab of the connection, with one tab the group's x closes it; with Lock, or the device's `AutoSignInEndAction` = Lock, "… is locked." and nothing asked

#### UI-AG-11 · 2-minute reconnect after automatic sign-in
P2 · How: U N · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- after SignedIn the device's network cut 60 s → the tab shows "Attempt n – trying for up to 120 s." and resumes the same session; past 120 s: "The connection dropped and couldn't be restored within 120 s: …"; a normal session keeps 30 s (UI-NET-01)

#### UI-AG-12 · Password checks before sending
P3 · How: U · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- no stored password → "Automatic sign-in needs a stored password for this connection – sign in on the device's screen instead."; over 256 characters → "The password is too long for the automatic sign-in – …"; nothing sent, no "Requested" audit entry

#### UI-AG-13 · Agent test fails → offer to install
P2 · How: U PS · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- agent stopped, *Test RemoteHub Agent* → "The RemoteHub Agent on <name> could not be used. <reason>" with *Install / update the agent…*; the entry then reads "Test RemoteHub Agent again", tooltip "Last result: <reason>"

#### UI-AG-14 · Install dialog checks its fields
P3 · How: U · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- port 0 / x → "The port must be a number from 1 to 65535."; scope "abc" → "\"Allow connections from\" must be IP addresses or ranges …"; empty label → "Enter a device label."; the dialog stays open

#### UI-AG-15 · Installed but this PC can't reach it
P2 · How: U PS · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- scope leaving out the admin's PC → after 5 tries "Installed, but this PC can't reach the agent on <host>:<port> (<error>). Check firewalls …"; still marked installed

#### UI-AG-16 · Throttled sign-in in the UI
P3 · How: U · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- after repeated wrong passwords the tab shows "The RemoteHub Agent on <host>:58732 refuses sign-ins from this PC for a few minutes after too many failed attempts."

#### UI-AG-17 · Uninstall from the UI
P2 · How: U PS · Needs: – · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Expected:**
- right-click → RemoteHub Agent → *Uninstall RemoteHub Agent…*; the question names "its credential provider, certificate and firewall rule" (and the connections reached through it, if any); *Uninstall*
- msiexec /x runs on the device through the agent's own PowerShell, detached; RemoteHub waits until the agent has been silent for 20 s: "Uninstalled – <name> no longer runs the RemoteHub Agent.", *Done*
- on the device: no `RemoteHubAgent` service, no "RemoteHub Agent" in Apps, no `RemoteHub Agent*` firewall rule, no credential provider key; `%SystemRoot%\Temp\RemoteHub-Agent-uninstall.log` written
- the connection's menu offers *Install RemoteHub Agent…* again (no *Update* / *Uninstall*); audit "Agent uninstalled" "Uninstalled"

#### UI-AG-18 · Remove here only
P3 · How: U PS · Needs: – · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Preconditions:** the device runs an agent with a certificate RemoteHub didn't make (after UI-AG-03).

**Expected:**
- *Uninstall RemoteHub Agent…* → *Uninstall* fails ("Failed: …"); then "The agent couldn't be uninstalled from <name>: … Remove it from this connection in RemoteHub only? The agent stays on the device; uninstall it there in Settings → Apps." → *Remove here*
- the connection forgets the agent (menu: *Install RemoteHub Agent…*), the device still runs it; audit "Agent uninstalled" "Removed here only – the device wasn't changed"

#### UI-JUMP-01 · RDP tab through a jump host
P1 · How: U · Needs: – · Status: ✅ Automated – `UI-RDP.Tests.ps1`

**Expected:**
- **2VM** + jump host: connection under the jump host → RemoteHub asks "Trust this RDP certificate?" ("<name> is reached through <jump> …", issued to the device's computer name, thumbprint) → *Trust and connect* → RDP signs in with NTLM through the forwarder (4624 NTLM), no certificate warning from the RDP client
- Steps: see the test (`lab/tests/UI-RDP.Tests.ps1`).

#### UI-JUMP-02 · RDP certificate pin
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-JUMP.Tests.ps1`

**Expected:**
- the second connect asks nothing; a different certificate → "RDP certificate changed" / *Trust the new one*; Cancel → no tab
- closing the tab after *Trust the new one* leaves no RDP tab within 15 s, and RemoteHub still answers UI Automation

#### UI-AD-09 · No usable session
P2 · How: U PS · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- nobody signed in, no SecureDesktop right → entries end " – your account may not see it" and are off, "Nobody is using a session right now, and your account may not see the sign-in or lock screen …", *Connect* off

#### UI-AD-10 · Consent choices per session
P3 · How: U · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- own signed-in session: "Connect without asking (they see a banner)" offered even when the policy lacks None; locked / sign-in session: "When someone unlocks or signs in: ask them / show them a code / don't ask (they see a banner)"

#### UI-CP-13 · DesktopClipboard=2 (images)
P3 · How: U S PS · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- an image reaches the device; a file copy is refused with "Copying files isn't allowed on this device." (the agent's wording; RemoteHub said "Copying images and files isn't allowed on this device – only text." up to 0.20.3)

#### UI-CP-14 · Copies from before connecting are quiet
P3 · How: U S · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- a too-big or refused copy already on the clipboard when the tab goes live → no notice; the same copy while connected → the notice

#### UI-CP-15 · Cancel a copy to the device
P3 · How: U S · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- 1 MB or more: "Copying <what> to the device", cancel → "Cancelled – nothing was copied to the device."; no receipt within 30 s → "Failed – the device didn't confirm the copy of <what>; it may not have arrived."

Note: the 99 MB go through the admin's PC's adapter limited to 10 Mbit/s. Until lab run 20261019-193627 the device's adapter was limited – Hyper-V limits only what a VM sends, so that never slowed a copy to the device; in that run the device logged the paste ("… comes now") but RemoteHub never showed "Copying 1 file to the device", and the agent noticed a dropped connection about 2.5 min later. Not explained by the evidence: RemoteHub's log now records each paste request from the device and what became of it, the agent's log a paste request that isn't passed on, and the test keeps RemoteHub's log lines (`cp15ClientLog`) when the progress doesn't come.

#### UI-TL-07 · WinRM account refused
P3 · How: U · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- "PowerShell remoting answers on <name>, but the account was refused. <msg>", no *Set up on the target…*

#### UI-TL-08 · Untrusted HTTPS from the remoting test
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- "<name> answers over HTTPS, but this PC doesn't trust its certificate yet. …" → *Check the certificate…* → "Trust this certificate?" / *Same – trust it*; declined → "Not trusted. Nothing was changed on this PC."

#### UI-TL-09 · Copy script
P3 · How: U · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- *Test again* only after Copy or Save; "Copied. Open an elevated PowerShell on <host>, paste … Then click \"Test again\" …"; clipboard kept out of history, cleared after 120 s

#### UI-TL-10 · File pane filter
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Expected:**
- *Filter This PC* "UpFol" in `C:\Users\Public` → only UpFolder is listed and selected; Enter opens it (*Path This PC* = `C:\Users\Public\UpFolder`); the filter is empty in the new folder

#### UI-TL-11 · Ctrl+F in a file pane
P3 · How: U · Needs: – · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Expected:**
- the focus on the device's side (its path box), Ctrl+F, then W I N on the console keyboard → *Filter <device>* = "win"; *Filter This PC* empty; the navigation search doesn't get it

#### UI-TL-12 · Registry value filter
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Expected:**
- in `HKLM\SOFTWARE\RemoteHubLab`, *Filter values* "FromUi" selects the value; Enter opens its edit dialog, whose *Value data* is "set in the registry tab"

#### UI-TL-13 · Services filter
P1 · How: U · Needs: – · Status: ✅ Automated – `UI-AGENT.Tests.ps1`

**Expected:**
- *Filter* "Spooler" selects Print Spooler at once – the services list allows one selection only, and clearing its selection used to throw – RemoteHub keeps running, *Stop the service* is enabled before any row is clicked

#### UI-NET-03 · Agent reconnect gives up
P2 · How: U N · Needs: – · Status: ✅ Automated – `UI-MORE.Tests.ps1`

**Expected:**
- cut longer than 30 s (normal session) → "Connection lost – reconnecting…", "Attempt n – trying for up to 30 s.", then "The connection dropped and couldn't be restored within 30 s: <error>"; *Stop reconnecting* ends it at once ("Disconnected.", not the 30 s message; the lab allows 10 s after the click for its own UI round trips)

### 10.6 Recordings

#### C-REC-07 · Record a network-share tab
P3 · How: U PS · Needs: 2VM · Status: ✅ Automated – `C-REC2.Tests.ps1`

**Expected:**
- `<connection> - <share name> - <date time>.mp4`

#### C-REC-08 · Tab hidden while recording
P3 · How: U PS · Needs: 2VM · Status: ✅ Automated – `C-REC2.Tests.ps1`

**Expected:**
- every frame of the hidden period shows "Tab not visible – recording continues when it is shown again" – not the remote screen

#### C-REC-09 · Normal exit while recording
P2 · How: U PS · Needs: 2VM · Status: ✅ Automated – `C-REC2.Tests.ps1`

**Expected:**
- recordings finish (up to 10 s), the file plays, no dialog

#### C-REC-10 · "Recording saved"
P3 · How: U · Needs: 2VM · Status: ✅ Automated – `C-REC2.Tests.ps1`

**Expected:**
- file, `mm:ss · size`, folder; Play / Show in folder / Close / *Delete recording…* (to the Recycle Bin)

#### C-REC-11 · Unsafe connection names
P3 · How: PS · Needs: – · Status: ✅ Automated – `C-REC2.Tests.ps1`

**Expected:**
- invalid characters → `_`, cut at 80 characters, a second file ` (2)`

### 10.8 Notes

#### UI-NOTE-01 · View rail
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-NOTES.Tests.ps1`

**Expected:**
- *Notes* on the rail shows the Notes view (*Add page*); *Remote Control* shows the connection list; *Notes* again shows the notebooks

#### UI-NOTE-02 · New page
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-NOTES.Tests.ps1`

**Expected:**
- *Add page* in *Quick Notes*, a title typed into *Page title* → the page is listed in the section

#### UI-NOTE-03 · Paste into a script block
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-NOTES.Tests.ps1`

**Expected:**
- a script block from the format bar, a PowerShell line on the clipboard, Ctrl+V in the block → the block holds the line

#### UI-NOTE-04 · View Markdown
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-NOTES.Tests.ps1`

**Expected:**
- *View Markdown* on → the *Markdown* text is the page as Markdown, the script inside a ```` ``` ```` fence; *Bold* greyed out; the toggle reads On

#### UI-NOTE-05 · Edit in View Markdown
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-NOTES.Tests.ps1`

**Expected:**
- `## Lab heading` added in the *Markdown* text, *View Markdown* off → the formatted page shows "Lab heading" (no `##`); the toggle reads Off

#### UI-NOTE-06 · Copy page as Markdown
P3 · How: U · Needs: – · Status: ✅ Automated – `UI-NOTES.Tests.ps1`

**Expected:**
- *Copy page as Markdown* → the clipboard holds Markdown text that starts `# <title>` and has the script

#### UI-NOTE-07 · Kept after a restart
P2 · How: U · Needs: – · Status: ✅ Automated – `UI-NOTES.Tests.ps1`

**Expected:**
- after closing and starting RemoteHub: Notes shows again, the page is listed, *Search notes* "Spooler" finds it, the script block still has the script, View Markdown is off

End to end, with what is stored (C-NOTE, `C-NOTES.Tests.ps1`):

#### C-NOTE-01 · View rail
P1 · How: U · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** RemoteHub freshly installed from the checkpoint, no RemoteHub data for the console account.

**Steps:**
1. Start RemoteHub as the console account; note which view it shows.
2. Look for the rail items Remote Control, Notes and Settings.
3. Click *Notes* on the rail.
4. Press Ctrl+1, then Ctrl+2.
5. Close RemoteHub and start it again.

**Expected:**
- The first start shows Remote Control; the rail has Remote Control, Notes and Settings.
- Notes shows "Notebooks and sections" with the notebook "Notes" and the section "Quick Notes"; Remote Control is hidden.
- Ctrl+1 shows Remote Control (Notes hidden); Ctrl+2 shows Notes.
- After the restart RemoteHub opens in Notes.

#### C-NOTE-02 · Notebook, section and page
P1 · How: U S · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-01; Notes shown.

**Steps:**
1. *New notebook*, name "Lab Notebook C2", OK.
2. *+ New section* in it (a real click), name "Lab Section<n>", OK.
3. *Add page*, set the title "Lab page Title<n>".
4. Read the notebooks, sections and pages from the database (session of the console account).
5. Restart RemoteHub (C-NOTE-04).

**Expected:**
- The notebook, the section and the page are shown; the page is listed under its title.
- In the database the section belongs to the notebook and the page to the section; the page is saved without a Save button.
- The audit log has `note.added` with the page's id.
- After the restart the notebook, section and page are listed again.

#### C-NOTE-03 · Typed formats and autosave
P2 · How: U S · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-02.

**Steps:**
1. *Add page* "Typed C3".
2. In the page (SendKeys in the session): `- alpha item` Enter `beta item` Enter Ctrl+. ; Ctrl+B `Body<n>` Ctrl+B ` and plain` Enter `# Typed heading`.
3. Wait for the page in the database.
4. *Copy page as Markdown* and read the clipboard.

**Expected:**
- Stored as `- alpha item`, `- beta item`, `**Body<n>** and plain` and `# Typed heading`, without any Save action.
- The search text (`body_text`) has the word but no `**` or `#`.
- The copied Markdown is `# Typed C3`, a blank line and the stored Markdown.

#### C-NOTE-04 · Round trip of every element
P1 · How: U S · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-02.

**Steps:**
1. *Add page* "Round trip C4"; put Markdown with a heading, bold, italic, strikethrough, inline code, a link, a check list, a quote, a table with a right-aligned column, an SQL block and a numbered list on the clipboard; page context menu → *Paste Markdown*.
2. Ctrl+End, then paste HTML as a browser copies it (CF_HTML): bold text, a red Comic Sans span, a `<script>`, a table and an `<img>` from `http://198.51.100.7` with alt text.
3. Read every page from the database; close RemoteHub (C-NOTE-07), start it again.
4. Open "Round trip C4": read the page's text (UI Automation) and *Copy page as Markdown*; open "Script C5", "Typed C3" and the C-NOTE-02 page, each longer than the autosave delay.
5. Read every page from the database again.

**Expected:**
- Every element is stored in its Markdown form (table row `| srv-c4-01 | 3389 | **main** |`, alignment `---:`, `- [ ]` / `- [x]`, `> `, a `sql` fence, `1.`).
- From the HTML: the bold word and the table; no `alert`, `<script`, colour or font; no link to `198.51.100.7`, its alt text instead.
- After the restart the Markdown is byte-identical; the page shows the table's cells; *Copy page as Markdown* gives `# Round trip C4` and the same Markdown.
- No page's Markdown, `modified_utc` or `version` changed by the restart or by opening it.

#### C-NOTE-05 · Script block, documentation only
P2 · How: U S PS · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-02; `C:\ProgramData\RemoteHubLab\c-notes-ran.txt` doesn't exist.

**Steps:**
1. *Add page* "Script C5"; put two lines of PowerShell on the clipboard (the first would create `c-notes-ran.txt`, the second is `Get-Service … | Where-Object …`); Ctrl+V into the empty page.
2. Read the block (UI Automation); *Copy script block* and read the clipboard.
3. Open the block's *More actions* menu and the page's context menu and list their entries.
4. Restart RemoteHub (C-NOTE-04), read the block again; look for `c-notes-ran.txt`.

**Expected:**
- A script block "Language: PowerShell"; stored as a `powershell` fence with the two lines exactly.
- *Copy script block* and the block's value give the text exactly, also after the restart.
- The menus have *Copy* and *Save as file…*, but no *Run*, *Execute* or *Send to …*.
- `c-notes-ran.txt` never exists.

#### C-NOTE-06 · Images
P2 · How: U S · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-02.

**Steps:**
1. *Add page* "Image C6"; put a 64 × 48 picture on the clipboard; Ctrl+V.
2. Write `C:\Users\Public\c-notes\photo-c6.jpg` (120 × 90, a JPEG comment segment with a marker) and put it on the clipboard as a copied file; Ctrl+End, Ctrl+V.
3. Read `note_files` for the page (mime, size, first 8 bytes, the marker's position).
4. Restart RemoteHub (C-NOTE-04).

**Expected:**
- Two images, both `image/png` starting `89 50 4E 47 0D 0A 1A 0A`, 64 × 48 and 120 × 90; the marker is not in either.
- The page's Markdown refers to both ids: `![Pasted image](note-file:…)`, `![photo-c6](note-file:…)`.
- After the restart the page opens again with both images shown.

#### C-NOTE-07 · Notes at rest
P1 · How: PS S · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-02 to C-NOTE-06 (pages with unique words in the title, section name, text, table cell, script block and pasted HTML).

**Steps:**
1. While RemoteHub runs: search every file under `%LOCALAPPDATA%\RemoteHub`, `%APPDATA%\RemoteHub` and `%TEMP%` of the console account for the words as UTF-8 and UTF-16 (files read with full sharing).
2. Close RemoteHub; search again and read the first 16 bytes of `remotehub.db`.
3. Read the pages, sections and audit log from the database.

**Expected:**
- The words are in the database (control) and in no file, while running (the WAL included) and after closing.
- `remotehub.db` doesn't start with `SQLite format 3`.
- No audit entry contains any of the words; the `note.*` entries are there.

#### C-NOTE-08 · Delete, Undo, Recently deleted
P2 · How: U S · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-02.

**Steps:**
1. *Add page* "Undo C8"; page context menu → *Delete*.
2. Click *Undo* on the toast.
3. Delete it again; wait until the toast goes.
4. *Recently deleted*: select "Undo C8", *Restore*, *Close*.

**Expected:**
- "Page deleted" with *Undo*; the page has `deleted_utc` set, and *Undo* clears it and lists the page again.
- The toast goes after about 8 seconds.
- *Recently deleted* lists the page; *Restore* brings it back.
- `note.deleted` and `note.restored` twice each, with the page's id.

#### C-NOTE-09 · A page deleted before its section
P2 · How: U S · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-02.

**Steps:**
1. New section "Doomed C9" with the pages "C9 early" and "C9 late".
2. Delete "C9 early"; then the section (context menu → *Delete…*, confirm).
3. *Recently deleted*: select "Doomed C9", *Delete now*, confirm.
4. Select "C9 early", *Restore*.

**Expected:**
- "Delete the section "Doomed C9" and its 1 pages?…"
- "C9 early" has an earlier stamp than the section; "C9 late" has the section's.
- After *Delete now* the section and "C9 late" are gone from the database; "C9 early" is still there (deleted) and still listed.
- *Restore* puts "C9 early" into a live section.

#### C-NOTE-10 · Search
P3 · How: U · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-04, C-NOTE-05; a page "Gone Zulu<n>" made and deleted.

**Steps:**
1. Type into *Search notes*: the table cell's word; its prefix; the script block's word; "Zulu<n>"; `"Zulu<n>`.
2. Esc.

**Expected:**
- The cell's word and its prefix: "1 page found", "Round trip C4" listed; the script's word: "1 page found", "Script C5".
- The deleted page's word and the word after a quote: "No pages found", no error.

#### C-NOTE-11 · `AllowNotes` policy
P2 · How: U PS · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** Notes shown, pages from C-NOTE-02.

**Steps:**
1. `HKLM\SOFTWARE\Policies\RemoteHub` `AllowNotes` = 0 while RemoteHub runs; open and cancel Settings (the window is activated again).
2. Ctrl+2.
3. Restart RemoteHub; count the pages in the database.
4. Remove the value; restart RemoteHub.

**Expected:**
- Remote Control is shown, the Notes rail item is gone, Ctrl+2 does nothing.
- After the restart: Remote Control, no Notes item; the pages are all kept.
- Without the policy RemoteHub opens in Notes again (the saved last view) with the pages listed.

#### C-NOTE-12 · Notes in a backup
P2 · How: U C S · Needs: – · Status: ✅ Automated – `C-NOTES.Tests.ps1`

**Preconditions:** C-NOTE-02 to C-NOTE-09 (pages with images).

**Steps:**
1. *Settings → Security & storage → Export backup*, save `C:\lab\c-notes.rhbackup`, a test password twice.
2. Search the file for the note words.
3. Close RemoteHub, delete its data, start it (fresh profile).
4. *Import backup*, the file, the password, *Add as folder*; show Notes.

**Expected:**
- "Backup saved to C:\lab\c-notes.rhbackup …"; "Backup exported" ends "…, N pages" (N = the pages not deleted).
- The backup file holds none of the words.
- The fresh profile starts in Remote Control; after the import the notebooks are there as "<name> (c-notes)" (no own "Notes" is made: it is made only when Notes first opens with no notebook).
- Every page is there with the same Markdown (image ids new) and every image with the same SHA-256.
- "Backup imported": "Added as a folder, and N pages".

### 10.9 First start and navigation

RemoteHub alone in the client VM, driven by UI Automation in `Admin`'s session. Test files: `C-FIRST.Tests.ps1`, `C-FAV.Tests.ps1`. Every other console test closes the welcome tour (or What's new) as a user would: `Start-LabClient` and the files' own start helpers call `Close-LabFirstStart`.

#### C-FIRST-01 · Welcome tour on a new install
P2 · How: U · Needs: – · Status: ✅ Automated – `C-FIRST.Tests.ps1`

**Expected:**
- the first start after a new install opens "Welcome to RemoteHub"; its first page has no *Back*

#### C-FIRST-02 · Tour pages
P2 · How: U · Needs: – · Status: ✅ Automated – `C-FIRST.Tests.ps1`

**Expected:**
- *Next* goes through 5 pages: the welcome, *Add your connections*, *The RemoteHub Agent*, *Notes*, *Keep it safe*; the last page's button says *Get started* and closes the tour

#### C-FIRST-03 · Shown once
P2 · How: U · Needs: – · Status: ✅ Automated – `C-FIRST.Tests.ps1`

**Expected:**
- the next start opens neither the tour nor What's new (UiState.LastSeenVersion)

#### C-FIRST-04 · From Help & about
P3 · How: U · Needs: – · Status: ✅ Automated – `C-FIRST.Tests.ps1`

**Expected:**
- *Settings → Help & about → Welcome tour…* opens the tour; *What's new…* opens "What's new in RemoteHub <newest entry>" with "Installed: <version>" under it – a patch without an entry of its own shows the last one there was

#### C-FIRST-05 · Pages left out by policy
P3 · How: U PS · Needs: – · Status: ✅ Automated – `C-FIRST.Tests.ps1`

**Expected:**
- with `AllowNotes` = 0 and `AllowAgent` = 0 the tour from Help & about has 3 pages, none about Notes or the agent

#### C-FIRST-06 · What's new after an upgrade
P2 · How: U PS · Needs: – · Status: ✅ Automated – `C-FIRST.Tests.ps1`

**Preconditions:** the previous release installed and started once with an .rdp file (a connection).

**Expected:**
- after installing this version, the first start never shows the tour
- with What's new entries newer than the previous release: What's new, "What's new in RemoteHub <newest entry>" with those highlights and *All release notes*
- with none newer (previous 0.21.0, newest entry 0.21.0): nothing opens

#### C-FIRST-07 · What's new once
P2 · How: U · Needs: – · Status: ✅ Automated – `C-FIRST.Tests.ps1`

**Expected:**
- the next start shows neither window

#### C-FAV-01 · Pin a connection
P2 · How: U · Needs: – · Status: ✅ Automated – `C-FAV.Tests.ps1`

**Expected:**
- folder *Lab* (Ctrl+Shift+N), connection *Alpha* in it (Ctrl+N with the folder selected); right-click *Alpha* → *Add to favourites* → a *Favourites* group at the top with a link; *Alpha* shows twice (the link and the connection in *Lab*)

#### C-FAV-02 · The link's menu and Edit
P2 · How: U · Needs: – · Status: ✅ Automated – `C-FAV.Tests.ps1`

**Expected:**
- the link's menu offers *Remove from favourites* and no *Delete* or *Duplicate*; F2 on the link opens *Edit connection* with display name *Alpha*

#### C-FAV-03 · Kept after a restart
P2 · How: U · Needs: – · Status: ✅ Automated – `C-FAV.Tests.ps1`

**Expected:**
- after RemoteHub restarts the *Favourites* group is there

#### C-FAV-04 · Del unpins
P2 · How: U · Needs: – · Status: ✅ Automated – `C-FAV.Tests.ps1`

**Expected:**
- Del on the link: no question, the group is gone, *Alpha* shows once (in *Lab*)

#### C-FAV-05 · Deleted with its folder
P3 · How: U · Needs: – · Status: ✅ Automated – `C-FAV.Tests.ps1`

**Expected:**
- *Alpha* pinned again, then *Lab* deleted (right-click → *Delete*, confirmed): the group is gone, *Alpha* doesn't show

#### C-RAIL-01 · The rail's version
P3 · How: U · Needs: – · Status: ✅ Automated – `C-FAV.Tests.ps1`

**Expected:**
- clicking the version on the rail opens *About RemoteHub*, not the update window

## 12. Security – attack cases (SEC)

The cases of [test-lab-cases.md](test-lab-cases.md) §13: the lab plays the attacker. Design: [security-test-lab-design.md](security-test-lab-design.md).

#### SEC-UPD-01 · Update with the right checksum, broken signature
P1 · How: U M PS · Needs: the lab signing certificate · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Preconditions:** client VM from the checkpoint; RemoteHub installed with `Install-LabClient -Network` (the VM trusts the lab signing certificate that signed the build); the mock update server serves the build's MSI and `RemoteHub-99.0.0-x64.msi`, a rebuild of it (`New-LabRebuiltMsi`: new ProductCode, so its signature no longer matches).

**Steps:**
1. The mock announces 99.0.0 with the rebuild, its real SHA-256 and size.
2. RemoteHub: *Settings → Updates & privacy → Check for updates now → Download and install*.
3. Watch for Windows' approval prompt (`consent.exe`) for up to 2 minutes while reading the update window.

**Expected:**
- the update window says "The downloaded installer has a signature that doesn't match the file or that this PC doesn't trust. It was deleted and won't be installed."
- Windows never asks for approval
- no `.msi` in `C:\Users\Admin\AppData\Local\Temp\RemoteHub\Updates`

#### SEC-UPD-02 · Download swapped during the approval prompt
P1 · How: U M C PS · Needs: the lab signing certificate · Status: ✅ Automated – `C-POLUPD2.Tests.ps1`

**Preconditions:** as SEC-UPD-01; the mock announces 99.0.0 with the build's own (signed) MSI; the rebuild copied into the VM.

**Steps:**
1. *Check for updates now → Download and install*; wait for Windows' approval prompt.
2. While it is shown, copy the rebuild over the downloaded MSI in the user's `%TEMP%\RemoteHub\Updates` (as the user's own programs could).
3. Approve (Alt+Y). Wait until RemoteHub runs again and read the message in the *RemoteHub updates* dialog.
4. Read `C:\Program Files\RemoteHub Updates` (its permissions, `*.msi`, `results\*.txt`) and the installed version.
5. Again without the swap: *Download and install*, approve, wait until RemoteHub runs again.

**Expected:**
- the approval prompt comes while RemoteHub still runs
- after the swap: RemoteHub starts again and says "The update wasn't installed: the downloaded file changed after RemoteHub had checked it…"; the outcome file says `refused changed`; no `.msi` in the staging folder; its permissions are not inherited and name only SYSTEM and Administrators; the installed version is unchanged
- without the swap: the newest outcome is `installed 0` (or 3010), no `.msi` left, RemoteHub runs again and shows no update message

#### SEC-VDD-01 · Rogue display-driver pipe
P1 · How: H S PS E · Needs: normaluser signed in at the console · Status: ✅ Automated – `SEC-VDD.Tests.ps1`

**Preconditions:** device VM with the agent (`-AutoLogon User`), `virtualDisplay` = `always` in agent.json.

**Steps:**
1. Connection A (headless desktop client) asks for 1280×720: the virtual monitor goes on; A stays connected.
2. In normaluser's session, a script serves `MTTVirtualDisplayPipe` with 4 instances and records, per connection, the bytes it reads and the account `GetImpersonationUserName` gives.
3. Connection B asks for 1600×900: the device is on already, so the agent sends the driver its reload command. B and A end.
4. Read the rogue script's record and the agent log since step 3.

**Expected:**
- normaluser could create the pipe name (the attack's precondition)
- no rogue connection received a byte; none could act as SYSTEM
- if a rogue instance was reached, the agent log says the control pipe "is served by another program"

#### SEC-VDD-02 · Settings folder at the drive root
P1 · How: H PS · Needs: – · Status: ✅ Automated – `SEC-VDD.Tests.ps1`

**Preconditions:** as SEC-VDD-01.

**Steps:**
1. A connection asks for 1280×720; read `C:\VirtualDisplayDriver`'s attributes, owner and permissions.
2. As normaluser, try to remove the folder and to create a file in it.
3. As the admin account, replace the folder with a junction to `C:\Users\Public\rh-sec-vdd-target`.
4. A connection asks for 1600×900; read the folder and the junction's target again.

**Expected:**
- after step 1: a real folder, permissions not inherited, owner SYSTEM or Administrators, only they may write
- normaluser can neither remove it nor create files in it
- after step 4: nothing in the junction's target; `C:\VirtualDisplayDriver` is a real folder again with those permissions, and its `vdd_settings.xml` has width 1600

#### SEC-SH-02 · SMB signing with RemoteHub's own client
P1 · How: H PS · Needs: Windows Server VM (share servers) · Status: ✅ Automated – `S-SHARE.Tests.ps1`

**Preconditions:** the S-SHARE shares; the server's `RequireSecuritySignature` set to `$false` (restored afterwards).

**Steps:**
1. The S-SMB-02 run: the admin through Windows' client, normaluser through RemoteHub's own client, which writes 14 files.

**Expected:**
- the second connection is RemoteHub's client and writes all 14 files

Note: Windows doesn't report a session's signing. RemoteHub's client signs every request and drops answers without a valid signature, so the copy only works because the server signs its answers to signed requests. The unit test `RemoteHubs_SMB_client_always_signs_and_refuses_a_guest_session` shows the client asks for signing.

#### SEC-LOG-01 · Nothing identifying in DiagLog
P1 · How: H E · Needs: the jump host VM · Status: ✅ Automated – `J-JUMP.Tests.ps1`

**Preconditions:** the J-JUMP file's setup; the time it ended is the start of the log read.

**Steps:**
1. Run the file's cases (jump sessions, forwarders, WinRM, shares).
2. Read this PC's DiagLog (`%APPDATA%\RemoteHub\logs\remotehub.log` and `.old`) from the start time on, with `Get-LabHostDiagLog`.

**Expected:**
- at least one "Jump host forwarder <8 hex characters>" line
- no line names the device's computer name, its address, the jump host's address, the account or the lab password (the evidence keeps only counts)

#### SEC-ENR-03 · A copied enrollment file
P1 · How: H PS · Needs: a second device VM (`Get-LabDevice`) · Status: ✅ Automated – `SEC-ENR.Tests.ps1`

**Preconditions:** both VMs reverted. One `.rhagent` made on the host with the lab's enrollment secret (`New-LabEnrollment`), kept outside the evidence folder.

**Steps:**
1. Read the file's fields (only their names go into the evidence).
2. Install the agent on the VM and on the second device with that same file (`Install-LabAgent -EnrollmentFile`) – the copy taken before it was used.
3. Read both devices' state (`Get-LabAgentState`: device ID, certificate, whether its key can be exported).
4. Claim each device as RemoteHub does (`Get-LabAgentClaim`, `AgentInstaller.ClaimAsync`).
5. Claim the second device under the first one's computer name (a DNS name pointing at it); ask the second device's health with the first one's pin.
6. Claim both again as RemoteHub would after *Replace and revoke* (`-RevokeCurrent`).

**Expected:**
- the file has `secret` and no `pfx` or `deviceId`
- the two devices have different device IDs and certificates; neither key can be exported; `enrollmentKeyId` in `agent.json` is the file's
- both claims succeed (`Claimed`), each with the certificate its own device holds – the first one's is the pin read back at the install
- the second device under the first one's name: `OtherName`; the first one's pin is refused by the second device
- after the revocation both claims: `RevokedSecret`

#### SEC-DEC-06 · The decoder sandbox on the installed build
P1 · How: PS U · Needs: – · Status: ◐ Written – `SEC-DEC.Tests.ps1`

**Preconditions:** the device VM reverted; RemoteHub installed from the MSI under test; normaluser signed in at the console.

**Steps:**
1. In normaluser's session: run `"C:\Program Files\RemoteHub\RemoteHub.exe" --decoder-check <file>` and wait for it to end.
2. Read the JSON it wrote.

**Expected:**
- exit code 0, no `error`
- `confinement` and the probe `sandbox`: `AppContainer`
- the video: as many pictures shown as sent, or one fewer (a picture may come out only with the next at the stream's format change), mean difference below 5 (of 255)
- the PNG, BMP and JPEG images: each `64×48`
- every other probe `blocked`: network, loopback, the user's profile, RemoteHub's database folder, writing in the profile, writing HKCU, the clipboard, starting a process, opening RemoteHub's process, using another process's window
