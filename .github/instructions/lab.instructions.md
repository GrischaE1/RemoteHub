---
applyTo: "lab/**/*.ps1,lab/**/*.psm1,lab/**/*.psd1,lab/**/*.json,build/**/*.ps1"
---

# Test lab and build scripts (PowerShell)

The lab runs Pester tests against provided Hyper-V VMs, as the standard user `labop` from a scheduled task. Spec:
`docs/test-lab-spec.md`; test cases: `docs/test-lab-cases.md`; test document: `docs/test-lab-test-document.md`.

- Each `lab/tests/<ID>.Tests.ps1` starts with a comment naming the case ID, what it covers and the section of
  `docs/test-lab-cases.md` it implements, and takes `-VmName` and `-EvidenceDir` parameters. A new or changed test
  should update that section of the cases document (and the test document) in the same PR.
- Shared helpers belong in `lab/RemoteHub.TestLab/RemoteHub.TestLab.psm1` (exported through the `.psd1`), not copied
  between test files. The module uses `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`.
- Tests must not need elevation or an interactive sign-in on the host, and must not depend on state from an earlier
  test file – every file starts from the VM's checkpoint.
- Wait for conditions with a timeout (`Wait-LabHostFile`, the helpers' `-TimeoutSec`) instead of fixed
  `Start-Sleep` margins; a timeout should fail with a message saying what didn't happen.
- Evidence (JSON, screenshots, logs) goes to `$EvidenceDir` with the test's prefix (`d-clip-...`).
- The lab password comes from `lab/lab.local.json` (git-ignored). It must never be hard-coded, logged, written to
  evidence or committed; `lab.local.example.json` holds only a placeholder.
- Scripts must run on Windows PowerShell 5.1 unless they state otherwise (`shell: pwsh` in CI): no `??`, `?.`,
  ternaries or `&&` in 5.1 scripts. Write files with an explicit `-Encoding`.
- Build scripts (`build/`) run in CI on `windows-latest`; they should fail loudly (non-zero exit) rather than
  continue after an error.
