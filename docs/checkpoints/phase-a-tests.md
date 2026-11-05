# Checkpoint: Phase A — tests sub-session — 2026-11-05

## Environment
- OS / arch: Linux (Debian 13) x64 — dev host (A2 disclosure; see the src
  sub-session checkpoint for the host-shaping notes).
- .NET SDK (exact): 10.0.403 (pinned in `global.json`).
- Could execute builds/tests: **yes** — full solution builds 0 warnings /
  0 errors; **all tests executed here**, including real cross-process lock
  tests.

## Sub-session and phase status
- Sub-session: **tests**
- Phase status: **COMPLETE** for everything expressible on this host.
  Windows-only deliverables (below) remain recorded as **NOT RUN** /
  Windows-CI deliverables — they were never silently skipped.

## Decisions applied / assumptions made
- **`WinBrew.UnitTests` targets `net10.0-windows`** (recorded deviation
  from PLAN §4's plain `net10.0`): A16 requires exit-code mapping and JSON
  envelope coverage in UNIT tests; those types live in `WinBrew.Cli`
  (D9 places `ExitCodes.cs` there), and a `net10.0` test project cannot
  reference a `net10.0-windows10.0.22000` project (NU1201). The portable
  tests still run everywhere; the TFM exists only to reference the Cli
  assembly.
- **Locked-mode restore contract**: solution-level `dotnet restore
  --locked-mode` is THE stable command and passes here; it validates the
  `win-x64` and `win-arm64` graphs carried in the Cli/Shim locks.
  Per-RID `-r` locked restores of the full graph are not expressible
  (NU1004 exact-set matching; host-RID leakage) — CI recipe documented in
  ADR-0002. Lock files are host-shaped (linux-x64 section present); Phase F
  ci.yml must account for that.
- **Mutation lock gained a non-Windows guard file**
  (`winbrew.lock.guard`, ADR-0004): probed that .NET's Unix FileShare
  emulation only makes `FileShare.None` exclusive, so writer-writer
  exclusion needs an explicit flock on a SEPARATE file — an earlier
  attempt that flocked the main lock file blocked .NET's internal reader
  locks and was reverted. Windows behavior is pure D5; no guard file is
  ever created there.
- `ManifestArtifact/Dependency/Executable` became positional records (kept
  `with` semantics); required-property records suppressed their positional
  constructors, which made construction style inconsistent.
- DoctorUseCase now distinguishes "config.json missing" (all defaults,
  Ok) from "config.json invalid" (problems reported, Fail) — the real
  `JsonConfigStore.InspectAsync` returns a null document in BOTH cases.

## Files changed
| Area | Change |
|---|---|
| tests/WinBrew.UnitTests | 12 test files: ConfigDefinitions (registry completeness = exactly the D10 keys + defaults; range/type/enum/unknown/test-hooks rules), ManifestValidator (baseline validity; schemaVersion; STRICT normalized version; HTTPS homepage; SPDX structural grammar incl. AND/OR/WITH-exception + parens; scope/installer v1.0 rejections; artifact rules incl. duplicate arch, sha256 form, format↔installType pairing, loopback-HTTP only in test-hooks builds; executable name/target rules incl. '.exe' doubling, '../', absolute, backslash, drive letters; dependency id/range/self/dup/qualified), ExitCodes (full D9 table incl. 13 and 130, enum↔table exact coverage), EnvelopeSchema (cli-output.v1 invariants: ok/data/error/warnings shapes, single document, string enum statuses), PathList (missing value, order, case/slash-insensitive dedupe with ORIGINAL formatting preserved, 32,767 refuse / 2,047 warn, exact-entry removal), Identifiers, VersionOps (bare-range rejection reason, floating rejection, prerelease detection), JsonConfigStore (real temp files: missing/valid/unknown-key/invalid-JSON/testing.* report-vs-reject/atomic save/round-trip), FaultCheckpoints + wire round-trips, Interactivity matrix, DoctorUseCase (all checks incl. build-vs-OS warn, volume-conflict Fail, newer-schema Fail, pending-migration warn, live/dead/unknown holder, testing.* warn, test-hooks self-report, WINBREW_TEST_MODE in release) |
| tests/WinBrew.TestFixtures | Fakes.cs: FakeVersionInfo, FakeEnvironmentProbe (configurable), FakeStorageLayout, FakeConfigStore, FakeStateDatabase, FakeMutationLock |
| tests/WinBrew.IntegrationTests | MigrationTests (empty→v1 + REAL backup pre-state verified; all 13 planned tables; idempotency; newer-schema refusal exit-1; WAL mode; FTS5 test), LockProcessTests (real child processes: blocked process reports holding PID+command; lock frees on holder exit with no stale-breaking; metadata readable while held; PID-reuse start-time matching incl. missing/wrong/nonexistent), StorageLayoutTests (directory creation incl. cache\artifacts and state\backups; one-volume refusal names the directory; Inspect never creates) |
| tests/WinBrew.LockHolderHelper | NEW console helper (holds the mutation lock for N ms; prints its PID) — A16 real-process tests; added to the solution |
| src/WinBrew.Cli | InternalsVisibleTo UnitTests (envelope source-gen context); csproj unchanged otherwise |
| src/WinBrew.Infrastructure/Locking | ProcessMutationLock: guard-file flock for non-Windows writer-writer exclusion (ADR-0004); Windows path untouched |
| src/WinBrew.Core | Manifest records → positional (validator unchanged); ConfigDefinitions Integer keys now parse/return `int` (Long → `long`) for consistent persisted types; SPDX tokenizer rewritten (whitespace-aware full-coverage tokens); JsonConfigStore coerces JSON numbers to the declared D10 width |
| src/WinBrew.Application/Doctor | Missing-vs-invalid config distinction |
| docs/adr | ADR-0002: locked-mode restore contract; ADR-0004: guard-file design + probe evidence |

## Build and test results (actual output only)
| Command | Result | Evidence |
|---|---|---|
| `dotnet build WinBrew.sln -c Release` | PASS | 0 Warnings / 0 Errors |
| `dotnet test WinBrew.sln --filter FullyQualifiedName!~EndToEnd` | PASS | **UnitTests: 165/165 passed. IntegrationTests: 16/16 passed.** |
| `dotnet restore --locked-mode` (solution) | PASS | validates the whole graph incl. both published-RID sections |
| `dotnet restore --locked-mode -r <rid>` (graph-wide) | **N/A** | NU1004 — not expressible for RID-less dependency graphs; ADR-0002 documents the contract |
| EndToEndTests | **NOT RUN** | Phase F scope (needs published release binary on Windows runners); empty project by design this phase |

## Fault checkpoints covered
| Checkpoint | Test | Result |
|---|---|---|
| (none yet) | Registry remains empty by design (Phase B brings the first durable transitions: download.*, artifact.*); the seam + test-hooks build gate exist and the registry contract (authoritative enumeration, unknown-name throw) is unit-tested | n/a |

## Known gaps and deferred items (Windows-CI deliverables)
1. `wbrew --version`, `config *`, `doctor --json` from the **published zip**
   (`wbrew.exe`, `e_sqlite3.dll`, `wbrew-shim.exe`) — needs Windows.
2. Shim Native AOT publish per RID — cross-OS refused (PROBES.md §4).
3. No-%TEMP%-extraction assertion (A4/A16/A17) on the published binaries.
4. Registry-backed doctor paths (LongPathsEnabled, Path presence) — run on
   Windows; fakes cover the logic here.
5. E2E project intentionally empty until Phase F scaffolds CI/release.

## Concerns about locked decisions
- None new. The D5 lock's Windows semantics are unchanged; the guard file
  is additive on non-Windows only and documented in ADR-0004.
- PLAN §4's `WinBrew.UnitTests: net10.0` is superseded as recorded above —
  flagged for the PLAN owner to amend at Phase F review if desired.

## Next session must start with
- PROMPT.md + `phases/phase-b.md` + this checkpoint (and the src
  sub-session checkpoint for environment notes).
- Phase B scope: HTTP hardening, cache, A13 archive defenses, Native AOT
  shim (Windows publish), journal/recovery, `self install`, PATH (A12);
  first registered fault checkpoints (download.*, artifact.*, …).
- On Windows: clear the Phase A Windows-CI deliverables list above.
