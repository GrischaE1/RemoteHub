---
applyTo: "src/RemoteHub.CredentialProvider/**,tests/RemoteHub.CredentialProvider.Tests/**"
---

# Credential provider (C++)

This DLL is loaded by LogonUI and handles sign-in tickets that contain credentials. A crash or hang here can break
signing in to the device, so review it strictly. Design: `docs/credential-provider-design.md`.

- **Never block LogonUI.** All pipe I/O happens on the provider's background thread; a LogonUI callback that waits on
  the pipe, a lock held during I/O, or an unbounded wait is a bug.
- **Untrusted input.** Everything read from the pipe goes through the parser in `Wire.cpp`: check every length and
  count against the remaining buffer before reading, watch for integer overflow, and reject rather than truncate.
  Changes to the wire format need matching changes on the agent side (`src/RemoteHub.Agent`) and in `Fuzz.cpp`.
- **Verify the server.** The pipe client must check that the server is the agent service before trusting it.
- **Secrets.** Tickets and passwords are single use and wiped (`SecureZeroMemory`) as soon as they're consumed or
  dropped, including on error paths. They must not be logged.
- **COM rules.** Correct `AddRef`/`Release` pairing, `CoTaskMemAlloc` for memory handed to LogonUI, `HRESULT`s
  checked and propagated, no exceptions crossing the COM boundary.
- **Ownership.** RAII and smart pointers (`std::unique_ptr`, `std::shared_ptr`) for handles and memory; flag raw
  `new`/`delete` or handles that can leak on an early return.
- Code lives in `namespace rhcp`; header comments describe the contract and cite the design doc section.
- The build uses hardening flags and CI fuzzes the message parser for a minute
  (`build/build-credential-provider.ps1 -FuzzSeconds 60`); parser changes should extend `Tests.cpp` and the fuzzer.
