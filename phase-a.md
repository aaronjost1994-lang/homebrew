# WinBrew Implementation Prompt v2.3 — Phase A (CLI, configuration, schemas, state)

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

## Phase A — CLI, configuration, schemas, state

**Scope:** solution scaffolding, build props, central packages,
analyzers; `RuntimeIdentifiers` (`win-x64;win-arm64`) on `WinBrew.Cli`
and `WinBrew.Shim` plus per-RID lock files; the `WinBrew.Shim` Native
AOT build producing `wbrew-shim.exe` per RID; the publish-output
manifest `scripts/publish-files.txt`; fast-path `--version` /
`--help` handling before host/SQLite init and the lazy
`Lazy<IServiceProvider>` pattern (A4 anchor); the System.CommandLine
2.0 command tree for every A8 command (commands outside this phase
return a documented `NotAvailableInThisBuild` error through the normal
error path, exit 1, and are listed in the checkpoint); global options;
the consent and non-interactive model; JSON output envelope; exit-code
mapping (including 13); config load, validate, get, set and list; all
JSON Schemas in `schemas/` plus typed models and validators; SQLite
with migrations, backups and WAL; the mutation lock with full D5
semantics (holder metadata including `processStartTime`, torn-read
tolerance, backoff polling, no stale-lock breaking); the operation
journal tables; `doctor` (environment checks, lock inspection with
PID+start-time matching, build-vs-OS architecture warning, reporting of
test-only settings in release builds); `--version`; **the FTS5 probe**
(determine whether the bundled SQLitePCLRaw includes FTS5; record the
result in the checkpoint — it decides the D6 search implementation).

**Acceptance**
- `dotnet build` with warnings as errors and `dotnet test` pass.
- Unit tests cover config validation, manifest validation, exit codes,
  JSON envelope schema conformance and lock behavior across real
  processes (including reporting the holding PID on timeout, torn-read
  tolerance and PID-reuse detection).
- Restore in locked mode succeeds for every RID.
- Migration tests: empty → v1, and backup created.
- `wbrew --version`, `config *`, `doctor --json` work from a published
  zip build (`wbrew.exe`, `e_sqlite3.dll`, `wbrew-shim.exe`).
- The exact publish output for each RID is recorded in
  `scripts/publish-files.txt` (committed) and in the ADR; the release
  zip layout derives from it (A4).
