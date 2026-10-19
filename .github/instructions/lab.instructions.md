---
applyTo: "lab/**/*.ps1,lab/**/*.psm1,lab/**/*.psd1,lab/**/*.json,build/**/*.ps1"
---

# Test lab and build scripts (PowerShell)

The lab runs Pester tests against provided Hyper-V VMs, as the standard user `labop` from a scheduled task. Spec:
`docs/test-lab-spec.md`; test cases: `docs/test-lab-cases.md`; test document: `docs/test-lab-test-document.md`.

- Each `lab/tests/<ID>.Tests.ps1` starts with a comment naming the case ID, what it covers and the section of
  `docs/test-lab-cases.md` it implements, and takes `-VmName` and `-EvidenceDir` parameters. A new or changed test
  should update that section of the cases document (and the test document) in the same PR; the status column
  follows from the lab results with `lab\Update-LabCaseStatus.ps1`.
- Shared helpers belong in `lab/RemoteHub.TestLab/RemoteHub.TestLab.psm1` (exported through the `.psd1`), not copied
  between test files. The module uses `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`.
- Tests must not need elevation or an interactive sign-in on the host, and must not depend on state from an earlier
  test file – every file starts from the VM's checkpoint.
- Wait for conditions with a timeout (the module's `Wait-Lab*` helpers, e.g. `Wait-LabVm`, `Wait-LabDesktop`, or a
  helper's `-TimeoutSec`) instead of fixed
  `Start-Sleep` margins; a timeout should fail with a message saying what didn't happen.
- Evidence (JSON, screenshots, logs) goes to `$EvidenceDir` with the test's ID as a lower-case prefix.
- The lab password comes from `lab/lab.local.json` (git-ignored). It must never be hard-coded, logged, written to
  evidence or committed; `lab.local.example.json` holds only a placeholder.
- Scripts must run on Windows PowerShell 5.1 unless they state otherwise (`shell: pwsh` in CI): no `??`, `?.`,
  ternaries or `&&` in 5.1 scripts. Write files with an explicit `-Encoding`.
- Lab scripts are ASCII-only: 5.1 reads BOM-less UTF-8 as ANSI, so an en dash or other non-ASCII character can end a
  string or break parsing. Use plain hyphens, and `[char]0x....` where a non-ASCII character is needed in output.
- Build scripts (`build/`) run in CI on `windows-latest`; they should fail loudly (non-zero exit) rather than
  continue after an error.

## Running the lab from an AI session

A lab run takes from minutes to hours and runs by itself (the scheduled task); reading its raw output costs far more
tokens than it is worth. In a Claude Code or similar session:

- Let the person start runs – `lab\LabConsole.cmd` (choose tests and VMs, build, start, follow, see the failures) or
  `lab\Start-LabRun.ps1 -Tag …` in their own terminal – and say when one has finished. If the session starts one,
  run it in the background and wait for it to end – no monitors, no polling, no reading the queue or the logs while
  it runs.
- Every run ends with `artifacts\lab\<run>\failures.txt` (`Get-LabFailures.ps1`): one line per VM, one per failed
  case (a failed setup's error from the console log), and where the evidence is. Read that first – or run
  `lab\Get-LabFailures.ps1 [-Run <id>] [-Root D:\RemoteHubLab\results]` for an older run. Don't dump pester XMLs,
  console logs, `report.md` or evidence JSON whole.
- Open evidence only for the failure being diagnosed, and only the fields that matter (`Select-Object`, a JSON
  property, `-Tail`), never whole files of tens of KB. Look at a screenshot only when the text can't answer it.
- Re-run only what failed (`-Tag <case IDs>`), not the whole suite or whole files.
- Diagnose a few failures in the session itself; a subagent per area only when several unrelated failures need deep
  code reading (each costs about 100k tokens).
