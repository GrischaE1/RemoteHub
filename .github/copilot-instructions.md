# RemoteHub – review instructions

RemoteHub is a Windows remote administration hub (WPF, .NET 10) that stores credentials for servers. The RemoteHub
Agent runs as **LocalSystem** on managed devices and gives remote access to them. The credential provider is a C++ DLL
loaded by LogonUI. Treat every change through a security lens first.

## Layout

- `src/RemoteHub` – the WPF app (Fluent design via WPF-UI, CommunityToolkit.Mvvm, RDP through `mstscax.dll`,
  PowerShell 7.6 SDK for remoting, encrypted SQLite for the local database).
- `src/RemoteHub.Agent` – the agent service (LocalSystem), its desktop helper and tunnel.
- `src/RemoteHub.CredentialProvider` – C++ credential provider for auto sign-in, talks to the agent over a named pipe.
- `tests/RemoteHub.Tests` – xUnit tests; `tests/RemoteHub.CredentialProvider.Tests` – C++ unit tests and a fuzzer.
- `lab/` – Pester end-to-end tests that run on Hyper-V VMs; `installer/` – WiX v5 MSIs; `build/` – build scripts.
- `docs/` – design documents. Code comments cite them by file and section (e.g. `credential-provider-design.md §8.2`).

## What to look for, in order

1. **Secrets and privacy.** Passwords, tickets and keys must never reach logs, exception messages, telemetry, crash
   reports, the clipboard (except `SecureClipboard`), temp files or URLs. The app's `DiagLog` must not contain
   passwords, usernames or host names. The agent's `AgentLog` audit entries deliberately record the account and source
   address (and go to the Windows Application log) – keep those, but never secrets or command content. Secrets in
   memory should be wiped once used where the code already does so nearby.
2. **Privilege boundaries.** Anything the agent receives over the network or from a session helper is untrusted:
   check paths (traversal, reparse points, names leaving their folder), sizes and limits, and that policy, consent
   and view-only checks happen on the agent side, not only in the UI. Named pipes must verify the other end.
3. **Correctness** – threading (UI work on the dispatcher, STA where COM/clipboard needs it), disposal, cancellation,
   error paths that leave state half changed, upgrades from older settings (`RdpManager` names still need to work).
4. **Tests.** A behaviour change should come with a test in `tests/RemoteHub.Tests`, or a lab test when it needs a
   real device (agent, MSI, sign-in, secure desktop). Flag tests that depend on timing margins (`Thread.Sleep` as
   the only synchronisation) when a condition could be waited for instead.
5. **Docs in step.** If a change contradicts a design doc in `docs/` or the README's feature description, say so.

## Conventions

- Follow the code around the change: naming, comment style, how pages and dialogs are built. Don't suggest
  restructuring code the PR doesn't touch.
- Comments explain *why* or what something guarantees, in plain sentences; they don't narrate the code.
- User-visible text and docs use British English (colour, organised) and the en dash ( – ) for asides.
  Exception: lab PowerShell scripts stay ASCII-only (plain hyphens) – see the lab instructions.
- The build must have no warnings (`dotnet build RemoteHub.sln`); nullable reference types are on.
- Third-party code needs its source and license stated (AGPL-3.0 project with a CLA) and an entry in
  `THIRD-PARTY-NOTICES.md`.
- GitHub Actions are pinned to a full commit SHA with the version in a comment; keep it that way.
- `lab/lab.local.json` holds the lab password and must never be committed.

## How to comment

- Point at a concrete failure: the input or state, and what goes wrong. Skip style nits that the surrounding code
  doesn't follow either.
- Don't ask for XML doc comments on every member, `ConfigureAwait`, or other patterns the codebase doesn't use.
- One PR is one topic; mention unrelated changes mixed in, but don't review them as if they were the point.
