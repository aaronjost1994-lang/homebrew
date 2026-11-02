# WinBrew Implementation Prompt v2.3 — Phase E (Windows installer integration, v1.1)

> **Read `PROMPT.md` first** — it is sent in the same session and
> contains Part A (the design you must implement) and Part C (the
> checkpoint templates you must fill). This file adds only what is
> specific to this phase. `CHANGES.md` is reference material for the
> project owner and is never sent to implementation sessions.

Each phase session must:
1. Start by restating which decisions from Part A it touches, and any
   assumptions it makes.
2. Implement only its scope, in buildable steps.
3. **Context-window management:** **Phases A and B split into `src` and
   `tests` sub-sessions by default** (this is the plan, not an
   exception); any other phase splits the same way if it requires more
   than ~25 files or approaches context limits. In every split,
   complete all production code (`src/*` and `schemas/*`) first, verify
   `dotnet build` passes, and emit a mid-phase checkpoint report so
   `tests/*` can be completed in the immediate continuation session
   without truncating files. A mid-phase checkpoint is marked
   **INCOMPLETE** (Part C1); **a phase counts as complete only after
   its test sub-session passes and the final checkpoint says
   COMPLETE.**
4. Run the available builds and tests and report the real output.
5. End with a checkpoint report (Part C1).

## Phase E — Windows installer integration (v1.1)

**Scope:** MSI and EXE installer models and validation; the elevated
helper (A12) implemented exactly as specified: refuse untrusted
directories after checking owner **and** DACL, inspecting each parent
component on an opened handle (`FILE_FLAG_OPEN_REPARSE_POINT`) (never
repair, never adopt); create each directory with `CreateDirectoryW`
carrying its final protected security descriptor and explicit owner,
re-running the checks on `ERROR_ALREADY_EXISTS`; generate the `<op>`
name itself, copy the artifact in and hash it from a
`FileShare.Read`-only handle that stays open while the installer runs,
and never apply ACLs recursively to pre-existing content;
msiexec exit-code handling including **13 `InstallerBusy`** for `1618`
after retries; ProductCode tracking; reconciliation in `list` and
`doctor`; EXE detection rules; `--allow-installer` and
`--scope machine`; a WiX-built fixture MSI and a minimal fixture EXE
installer built in CI. Completes `self uninstall` for v1.1: it now
refuses when installer-managed packages are still installed (A14).

**Acceptance**
- Real install and uninstall of the fixture MSI (per-user and
  per-machine) and the fixture EXE on disposable runners.
- Each documented msiexec exit code maps correctly (unit tests plus at
  least success and failure in real runs), including `1618` → 13.
- TOCTOU and directory pre-creation tests: pre-creating
  `%ProgramData%\WinBrew` as a standard user — as a plain directory, as
  a junction, and with a hard link inside — causes refusal in every
  case; swapping the artifact after the user-side check is defeated by
  the held handle and hash-from-handle; **no ACL outside directories
  the helper created is ever changed** (verified by ACL snapshots
  before/after).
- An existing Administrators-owned directory granting `Users:Modify`
  is refused (the DACL check).
- A directory created between the helper's check and its create call
  is detected through `ERROR_ALREADY_EXISTS` and re-checked, never
  silently adopted.
- No code path builds a shell command string (enforced by an analyzer
  rule or a test).
