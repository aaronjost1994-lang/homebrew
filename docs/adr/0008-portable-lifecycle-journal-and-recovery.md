# ADR-0008: Portable lifecycle, journal and recovery (A11, D8)

**Status:** accepted (Phase B src sub-session, 2026-11-06)

## Decisions

1. **Step protocol.** Every durable transition is `BeginStep(intent)` →
   filesystem action → `CompleteStep(outcome)` → (optional state change) →
   fault checkpoint (`JournaledOperation.StepAsync`). The intent carries
   everything needed to undo or finish that action idempotently. A crash at
   a checkpoint finds the outcome recorded; a crash inside an action finds
   only the intent — both are handled because every compensating and
   roll-forward action is idempotent.
2. **Point of no return = `StateCommitted`.** It is written in the SAME
   SQLite transaction as the installed-state rows (files and SQLite are
   separate resources; only the SQLite side is atomic). Recovery rule:
   `StateCommitted` → roll **forward**; any earlier state (or
   `Compensating`) → **compensate** by walking the recorded intents in
   reverse. A failure inside recovery marks the operation `RecoveryFailed`
   with an exact report; the command exits 8 and never claims success.
3. **Install/upgrade order** (A11 steps 3–9): staging → manifest (digest
   bound to the catalog, exact catalog match) → download or verified cache
   hit → SHA-256 → cache → extract (A13) → validate (PE machine/subsystem,
   collisions) → place → shims (sidecar, then binary) → `StateCommitted` →
   post-commit (stale shims, `retainVersions` pruning, staging) →
   `Completed`. During an upgrade the previous version's directory and shims
   stay intact until `StateCommitted`; compensation restores the previous
   sidecar bytes recorded in the intent.
4. **Uninstall commits first.** Nothing on disk changes before the
   uninstall's `StateCommitted` transaction, so compensation is a no-op and
   everything after it (shims, `packages\<id>\`) is roll-forward only. A
   running shim's binary becomes a journaled `.old` file.
5. **Post-commit failures do not undo a commit.** If cleanup after
   `StateCommitted` fails, the command succeeds with a warning and the
   operation stays `StateCommitted` for the next mutating command or
   `wbrew recover` to finish.
6. **Startup recovery** runs inside `MutationSession.BeginAsync` (lock →
   layout → migrations → recovery → journaled-file cleanup) for every
   mutating command. Read-only commands (`list`, `info`, `doctor`) report
   pending recovery and `RecoveryFailed` operations as warnings and never
   act on them. `--dry-run` takes no lock and changes nothing.
7. **Checkpoint registry** (`FaultCheckpoints.All`): the PLAN §6 names that
   Phase B fires, plus three Phase B additions — `uninstall.shim.removed`,
   `uninstall.package.removed`, `selfinstall.app.copied`. Phase D names
   (`trust.root.accepted`, `selfupdate.app.copied`) are registered when
   Phase D fires them, so "crash at every registered checkpoint" stays
   executable in every phase. `WINBREW_FAULT_AT=<name>#N` targets the N-th
   firing of per-shim checkpoints.
8. **Self install** has no installed-state row: its `StateCommitted` is a
   journal-only marker after `app\<ver>\` and the self-shim are in place;
   the consented PATH change is the roll-forward part. PATH consent is
   decided **before** any change; in a non-interactive session without
   `--modify-path` the PATH is left alone with a warning (declining an
   offer is not a missing consent). An existing `app\<ver>\` with
   different bytes is refused (exit 6) — a running version is never
   overwritten.

## Evidence (Linux dev host, test-hooks build, 2026-11-06)

A scripted harness (`WINBREW_FAULT_AT` + `wbrew recover`, then DB ↔ `bin\`
↔ `packages\` ↔ `staging\` consistency checks) crashed install (fresh),
upgrade (with a template change, so the `.old` path is exercised) and
uninstall at every registered checkpoint: **28 crash points fired, 28
consistent after recover**; plus 24 crash-during-compensation cases
(`compensate.started`, `compensate.shim.reverted`,
`compensate.package.removed`, `rollback.completed`): **all consistent**;
self install crashed at `selfinstall.app.copied` (rolled back) and
`state.committed` (rolled forward).

**Committed (tests sub-session):** `CrashRecoveryTests` crashes the real CLI
at every registered checkpoint (a coverage table is asserted to match the
registry exactly) and checks the same invariant; 21/22 pass on Linux,
`path.updated` runs on Windows against a disposable HKCU key.
