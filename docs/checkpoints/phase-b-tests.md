# Checkpoint: Phase B — tests sub-session — 2026-11-06

## Environment
- OS / arch: Linux (Debian 13) x64 — dev host, **not Windows 11 (A2)**.
- .NET SDK (exact): 10.0.403.
- Could execute builds/tests: **yes** for everything portable — both the
  release and the test-hooks configurations, with real child processes,
  real SQLite, a real in-process HTTP server and real ZIP files. Win32
  behavior (AOT shim process, HKCU PATH, Restart Manager, Credential
  Manager, published-binary extraction check) is written as tests that
  **SKIP with a reason** off Windows — never silently passed.

## Sub-session and phase status
- Sub-session: **tests**
- Phase status: **COMPLETE** for everything expressible on this host. The
  Windows-only acceptance items below are committed tests that run on
  Windows CI (Phase F wires the published artifacts into them) and are
  recorded as **NOT RUN** here.

## Decisions applied / assumptions made
- **`tests/WinBrew.CliTestHost`** (new test helper): the `src/WinBrew.Cli`
  sources compiled as a framework-dependent console app (`wbrew-testhost`),
  referenced by the integration tests exactly like `LockHolderHelper`. The
  shipped CLI is a self-contained single-file Windows exe and cannot be
  referenced from a test project; linking the same sources keeps one code
  path. Crash tests therefore FailFast a REAL process (A16).
- **Local HTTP server = `HttpListener`, not Kestrel** (A5 says Kestrel):
  avoids an ASP.NET Core framework reference in the fixtures; the behavior
  under test is the client's. Recorded deviation.
- **`WINBREW_TEST_PATH_KEY`** (production change, test-hooks builds only,
  plus `WINBREW_TEST_MODE=1`): points the PATH editor at a disposable HKCU
  key so PATH tests never touch the real user PATH (A16). Compiled out of
  release builds like every other test-only behavior (A11).
- Per-root shim templates: each harness root gets `app\0.0.0-test\wbrew-shim.exe`
  plus the `bin\wbrew.shim.json` self-shim sidecar, the same lookup a real
  install uses (D4) — no shared mutable template between parallel tests.
- `TestHooksFact`/`TestHooksTheory`/`WindowsFact`/`WindowsArtifactFact`
  report SKIPPED with the reason when their prerequisite is missing.
- Child processes run with their working directory in the test's temp
  folder, so FailFast core dumps never land in the repository (`.gitignore`
  also ignores `core`/`core.<pid>` since the src sub-session).

## Product fixes found by the tests
1. **ManifestParser:** an absent optional `dependencies`/`executables`
   array deserialized as **null** (source-generated deserialization sets
   every init-only member, overriding `= []`), crashing the validator with
   `ArgumentNullException`. Absent now means empty (A9). Fixture manifests
   always wrote both arrays, so the src-session smoke runs missed it.

## Files changed
| Path | Change | Notes |
|---|---|---|
| tests/WinBrew.CliTestHost/ | new project | wbrew-testhost (CLI sources, framework-dependent) |
| tests/WinBrew.TestFixtures/FixtureRepository.cs | new | PE/ZIP builders, fixture repository (artifacts, manifests, unsigned index) |
| tests/WinBrew.TestFixtures/LocalHttpServer.cs | new | in-process server: files, redirects, status codes, chunked, drop-after-N |
| tests/WinBrew.TestFixtures/PlatformFacts.cs | new | honest skip attributes |
| tests/WinBrew.UnitTests/ShimRulesTests.cs | new | 36-tail quoting corpus × quoted/unquoted argv[0] + named cases, 206 limit, containment, sidecar round-trip |
| tests/WinBrew.UnitTests/PhaseBRulesTests.cs | new | URL policy, archive names, PE + A10 tables, index/manifest strictness, package request syntax |
| tests/WinBrew.IntegrationTests/CliHarness.cs | new | isolated root + fixture repo + real CLI child + consistency invariant |
| tests/WinBrew.IntegrationTests/CrashRecoveryTests.cs | new | crash at every registered checkpoint → recover → consistent |
| tests/WinBrew.IntegrationTests/LifecycleCliTests.cs | new | lifecycle, consent, dry-run, catalog-only, collisions, PE, download/metadata rejections |
| tests/WinBrew.IntegrationTests/MaliciousArchiveTests.cs | new | one archive per A13 defense |
| tests/WinBrew.IntegrationTests/HttpFetcherTests.cs | new | A13 client behavior against the local server |
| tests/WinBrew.IntegrationTests/SelfInstallTests.cs | new | A14 zip layout + RecoveryFailed |
| tests/WinBrew.IntegrationTests/WindowsOnlyTests.cs | new | PATH test hive, real AOT shim process, extraction verification |
| src/WinBrew.Core/Manifest/ManifestParser.cs | fix | absent optional arrays |
| src/WinBrew.Cli/ServiceConfigurator.cs | change | WINBREW_TEST_PATH_KEY (test-hooks only) |
| Dockerfile | change | test host in the restore layer; second test pass with -p:WinBrewTestHooks=true |
| WinBrew.sln, test csproj/lock files | change | new project, linked shim sources, xunit in fixtures (`IsTestProject=false`) |

## Build and test results (actual output only)
| Command | Result | Evidence (summary / counts) |
|---|---|---|
| dotnet build WinBrew.sln -c Release | PASS | 0 warnings, 0 errors |
| dotnet build WinBrew.sln -c Release -p:WinBrewTestHooks=true | PASS | 0 warnings, 0 errors |
| dotnet restore WinBrew.sln --locked-mode | PASS | |
| dotnet test (release build) | PASS | Unit **501/501**; Integration **72 passed, 46 skipped** (test-hooks-only and Windows-only, each with its reason) |
| dotnet test (test-hooks build) | PASS | Unit **501/501**; Integration **143 passed, 14 skipped** (Windows-only), 0 failed, ~2 min |
| docker build --target test | see note | release + test-hooks passes inside the pinned SDK image |
| Windows-only tests (14) | NOT RUN | need Windows (and WINBREW_SHIM_EXE / WINBREW_PUBLISHED_EXE for 8 of them) |

## Fault checkpoints covered
| Checkpoint | Test | Result |
|---|---|---|
| download.started, download.completed, artifact.verified, artifact.cached, extract.completed, validate.completed, package.placed, package.activated | CrashAtCheckpoint_ThenRecover_IsConsistent (Install, Upgrade) | PASS |
| shim.sidecar.replaced, shim.binary.replaced | … (Install, Upgrade with a new template, SelfInstall) | PASS |
| state.committed, operation.completed | … (Install, Upgrade, Uninstall, SelfInstall) | PASS |
| uninstall.shim.removed, uninstall.package.removed | … (Uninstall) | PASS |
| compensate.started, compensate.shim.reverted, compensate.package.removed, rollback.completed | … (CrashDuringRecovery) | PASS |
| recovery.failed | … (SabotagedRecovery) + Recover_ThatCannotRestoreState_EndsInRecoveryFailed_Exit8 | PASS |
| catalog.ingested | … (Ingest) | PASS |
| selfinstall.app.copied | … (SelfInstall) | PASS |
| path.updated | CrashAtPathUpdated_ThenRecover_RollsForward | **NOT RUN** (Windows, test hive) |

`CoverageTable_CoversEveryRegisteredCheckpoint_Exactly` fails the suite if
a checkpoint is ever registered without a crash scenario.

## Phase B acceptance (phase-b.md)
| Item | Status |
|---|---|
| Malicious-archive corpus fully rejected (one test per defense) | PASS — see notes 1–2 |
| Corrupt, oversized, truncated, interrupted downloads rejected; staging cleaned | PASS |
| Crash at every checkpoint + recover → consistent | PASS (21/22 here; path.updated on Windows) |
| Shim quoting corpus (incl. ` a`, `"x.exe"arg`, unterminated, tab, empty, overflow 206) | PASS (unit, linked ShimRules.cs); real-process exit-code/206/containment tests **NOT RUN** (Windows + AOT shim); Ctrl+C reaching the child → Phase F E2E |
| Upgrade while the shim runs, incl. replacing the shim binary with `.old.<guid>` | logic PASS (sidecar-only upgrade, journaled `.old` replacement, crash-safe); running-process variant **NOT RUN** (Windows) |
| PATH tests on a test hive: preserve entries, missing value, length limits | string rules PASS (Phase A unit tests); registry tests **NOT RUN** (Windows) |
| Extraction verification test + ADR record | test committed; **NOT RUN** (Windows + published binary) — ADR-0001 records it as pending |
| Install reads the catalog only | PASS (unindexed manifest never requested; dry-run makes zero HTTP requests; manifest/index mismatch → 5) |
| Self-shim → `app\` accepted; other → `app\` refused; `..` refused; WINBROW_ROOT ignored | PASS (unit containment); real-process variant NOT RUN (Windows) |

Notes:
1. `TotalCompressionRatio` cannot trigger before `EntryCompressionRatio`
   while both use the same `archive.maxCompressionRatio`: the running total
   ratio can never exceed the largest per-entry ratio. It stays as defense
   in depth; no archive can exercise it alone. Recorded, not hidden.
2. `EscapesStagingRoot` and `ReparsePoint` are unreachable through ZIP
   entries once the name rules and the symlink-mode check pass; they guard
   against future bypasses and are not separately exercisable without a
   test seam. Observed: .NET bounds a deflate stream by the DECLARED size,
   so a header that under-declares a bomb cannot expand it
   (`HeaderClaimingATinySize_NeverYieldsMoreBytesThanTheLimitsAllow`).

## Known gaps and deferred items
1. **Windows CI (Phase F):** run the 14 Windows-only tests; publish
   `wbrew-shim.exe` (AOT, both RIDs) and the release `wbrew.exe` first and
   set `WINBREW_SHIM_EXE` / `WINBREW_PUBLISHED_EXE`; record the extraction
   result in ADR-0001; shim binary size < 3 MB; Ctrl+C and piping through
   the shim in E2E.
2. Disk-full simulation (`IOException` 0x80070070 → exit 12) is mapped in
   code but not fault-injected yet — no `IOException` seam exists in the
   file layer; candidate for Phase C test work.
3. Restart Manager naming (exit 11) needs a Windows process holding a file
   with `FileShare.None`.

## Concerns about locked decisions
- None new. (A5's Kestrel → HttpListener is a test-fixture deviation only.)

## Next session must start with
- PROMPT.md + `phases/phase-c.md` + this checkpoint (and phase-b-src.md
  for the lifecycle design notes).
- Phase C: PubGrub resolver, architecture re-selection, pins, cycles, plan
  rendering, upgrade compensation — `install` currently refuses packages
  with dependencies (`NotAvailableInThisBuild`); `uninstall` already refuses
  dependents via `dependency_edges`.
- Run tests in BOTH configurations (`dotnet test` and
  `dotnet test -p:WinBrewTestHooks=true` after a test-hooks build).
