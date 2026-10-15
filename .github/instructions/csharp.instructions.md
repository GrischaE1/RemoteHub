---
applyTo: "src/RemoteHub/**/*.cs,src/RemoteHub.Agent/**/*.cs,tests/RemoteHub.Tests/**/*.cs,tests/RemoteHub.LabHarness/**/*.cs,src/**/*.xaml"
---

# C# and WPF

- .NET 10, `Nullable` enabled. Usings differ per project – check the `.csproj` before flagging a missing `using`:
  the app and `RemoteHub.Tests` disable `ImplicitUsings` but declare global usings (`<Using Include="..."/>`: `System`,
  `System.IO`, `System.Linq` …); the agent enables `ImplicitUsings`; `RemoteHub.LabHarness` has neither.
- File-scoped namespaces (`namespace RemoteHub.Services;`), `_camelCase` private fields, `sealed` where a class isn't
  meant to be derived from. Collection expressions (`[...]`) and target-typed `new` are used throughout.
- Many windows and dialogs are built in code (`Views/*.cs` deriving from `Wpf.Ui.Controls.FluentWindow`) rather than
  XAML. A new dialog should be built the way its neighbours are, with WPF-UI controls and the theme's resources –
  flag hard-coded colours, sizes or fonts where a theme resource exists.
- Aliases like `using Button = Wpf.Ui.Controls.Button;` resolve WPF vs WPF-UI clashes; keep them consistent.
- UI work must run on the dispatcher. Background work that touches the UI after `await` or from a timer/thread is a
  bug unless it marshals back.
- P/Invoke: check `SetLastError`, buffer sizes, handle ownership and that handles are closed on every path.
- App logging goes through `DiagLog.Write`; messages may include `ex.Message`, never secrets, usernames or host names.
- The agent logs through `AgentLog`. Its `Audit` entries intentionally record the account and source address and are
  forwarded to the Windows Application log for Intune/SIEM – don't flag those identities or ask to remove that sink;
  do flag passwords, tickets or command content in any agent log line.
- Agent code (`src/RemoteHub.Agent`) runs as LocalSystem: anything from the wire (`AgentProtocol`, `TunnelProtocol`,
  desktop transfers) is untrusted – validate lengths, counts, paths and kinds before acting, and enforce policy
  (`AgentPolicy`, consent, view-only) there.
- Settings and the local database must keep reading older data: renamed fields, the `RdpManager` folder and policy
  key, `*.rdpmbackup` imports.

## Tests (xUnit)

- Test names are sentences with underscores describing the behaviour:
  `Offered_files_are_fetched_only_when_a_paste_reads_them`.
- A short `/// <summary>` on the test class says what area it covers.
- Tests that need something the CI runner may lack (a clipboard, a desktop session) use `[SkippableFact]` with
  `Skip.IfNot(..., "reason")` instead of failing.
- Tests that share global state (clipboard, files in `%APPDATA%`) use the same `[Collection("...")]` as their peers.
- Temp files go under `Path.GetTempPath()` with a unique name and are deleted in `finally`.
- Prefer waiting for a condition (`SpinWait.SpinUntil`, an event) over a fixed `Thread.Sleep`.
