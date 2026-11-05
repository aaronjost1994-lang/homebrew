# Checkpoint: Phase B — src sub-session — 2026-11-06

## Environment
- OS / arch: Linux (Debian 13) x64 — **not the expected Windows 11 host
  (A2 disclosure)**; same dev host as Phase A (`EnableWindowsTargeting`).
- .NET SDK (exact): 10.0.403 (`global.json`).
- Could execute builds/tests: builds **yes** (release and test-hooks, 0
  warnings). The CLI runs on Linux as a framework-dependent test-hooks build
  (scratch output only); everything Win32 (shim process model, registry
  PATH, Restart Manager, Credential Manager, AOT publish) is **NOT RUN —
  needs Windows**.

## Sub-session and phase status
- Sub-session: **src**
- Phase status: **INCOMPLETE** — remaining: the **tests sub-session**
  (see "Next session") and the Windows-only deliverables.

## Decisions applied / assumptions made
- Touches D4 (shim), D5 (lock use), D6 (catalog ingest path only), D8
  (uninstall refusal), D9 (exit codes 4/5/6/8/10/11/12), D10 (download.*,
  archive.*, retainVersions, path.managed, trust.maxMetadataBytes), A6, A8,
  A10, A11, A12, A13, A14. New ADRs: **0007** (shim argv[0] parser,
  containment exception, process model, `.old` protocol), **0008**
  (journal, recovery, uninstall/self-install ordering), **0009** (HTTP,
  archive defenses, x86 fallback policy, Phase B unsigned ingest).
- Install scope = single packages with no dependencies; a catalog entry
  with dependencies → `NotAvailableInThisBuild` (Phase C). Installing a
  different version of an installed package is journaled as an **upgrade**
  (operation type `upgrade`) — this is what the Phase B acceptance
  "upgrade while the shim runs" exercises; `wbrew upgrade` itself stays
  Phase C (resolver).
- Uninstall commits SQLite first, then removes files (roll-forward only);
  post-commit cleanup failures warn instead of undoing a commit (ADR-0008).
- Shim binaries that must change are always renamed aside to a journaled
  `.old.<guid>` (works whether or not the shim is running).
- Self install: non-interactive without `--modify-path` leaves PATH alone
  with a warning (declining an offer ≠ missing consent); PATH integration
  off-Windows is reported, never faked.
- `IHttpClientFactory` needed `Microsoft.Extensions.Http` **10.0.14**
  (same servicing level as Hosting); lock files regenerated; solution
  `dotnet restore --locked-mode` passes.
- `DoctorUseCase` gained an optional `LifecycleDoctorChecks` (shim template
  + recorded template hashes, interrupted / RecoveryFailed operations,
  journaled `.old` files awaiting cleanup); existing tests unchanged.
- Release builds accept **no** repository metadata yet
  (`UnavailableMetadataVerifier`); test-hooks builds use
  `TestHooksUnsignedVerifier` + hidden `wbrew test-ingest`.

## Files changed
| Path | Change | Notes |
|---|---|---|
| src/WinBrew.Core/Archive/ArchiveEntryRules.cs | new | A13 name rules, symlink bits, containment, `ArchiveLimits` |
| src/WinBrew.Core/Pe/PeImage.cs | new | bounds-checked PE machine + subsystem parser |
| src/WinBrew.Core/Artifacts/HostArchitectureRules.cs | new | A10 selection order + PE acceptance table |
| src/WinBrew.Core/Net/UrlPolicy.cs | new | HTTPS/loopback gate, redirect decisions, origin, redaction |
| src/WinBrew.Core/Catalog/RepositoryIndex.cs | new | strict index.json model + validator |
| src/WinBrew.Core/Manifest/ManifestParser.cs | new | strict manifest parse + A9 validation |
| src/WinBrew.Core/Shims/ShimSidecar.cs | new | sidecar model (writer side) |
| src/WinBrew.Core/Operations/FaultCheckpoints.cs | changed | 22 registered checkpoints |
| src/WinBrew.Application/Ports/{IHttpFetcher,IPackageFileSystem,IShimStore,IStateStores}.cs | new | Phase B ports |
| src/WinBrew.Application/Lifecycle/* | new | settings, journaled step runner, journal payloads, `MutationSession`, `RecoveryService`, `PostCommit`, paths |
| src/WinBrew.Application/Packages/* | new | install/upgrade, uninstall, list, info, recover |
| src/WinBrew.Application/SelfManagement/SelfInstallUseCase.cs | new | A14 zip layout |
| src/WinBrew.Application/Catalog/IngestIndexUseCase.cs | new | final ingest path |
| src/WinBrew.Application/Doctor/{DoctorUseCase,LifecycleDoctorChecks}.cs | changed/new | Phase B doctor checks |
| src/WinBrew.Infrastructure/Http/SecureHttpFetcher.cs | new | A13 client, retries, limits, streaming SHA-256 |
| src/WinBrew.Infrastructure/Files/{PackageFileSystem,ArtifactCache}.cs | new | A12 retries/RM/disk-full; content-addressed cache |
| src/WinBrew.Infrastructure/Archive/SafeZipExtractor.cs | new | every A13 defense |
| src/WinBrew.Infrastructure/Sqlite/SqliteStateStore.cs | new | catalog, installed state, journal over schema v1 |
| src/WinBrew.Infrastructure/Trust/MetadataVerifiers.cs | new | release refusal / test-hooks unsigned verifier |
| src/WinBrew.Windows/{ShimStore,RegistryUserPathEditor,RestartManagerInspector,CredentialManagerProvider}.cs | new | Windows adapters |
| src/WinBrew.Shim/{Program,ShimRules,SidecarReader,Native}.cs | new/rewritten | full D4 shim (AOT, LibraryImport) |
| src/WinBrew.Cli/{PackageCommands,CliSelfLocation}.cs | new | install/uninstall/list/info/recover/self install (+ test-ingest) |
| src/WinBrew.Cli/{CommandTree,ServiceConfigurator,Output/*}.cs | changed | wiring, warnings in the envelope, payload contexts |
| Directory.Packages.props, Infrastructure csproj, lock files | changed | Microsoft.Extensions.Http 10.0.14 |
| tests/WinBrew.UnitTests/FaultCheckpointsTests.cs | changed | Phase A test asserted `download.started` was unregistered — now registered by design |
| docs/adr/0007–0009, this file | new | |

## Build and test results (actual output only)
| Command | Result | Evidence (summary / counts) |
|---|---|---|
| dotnet build WinBrew.sln -c Release | PASS | 0 warnings, 0 errors |
| dotnet build WinBrew.sln -c Release -p:WinBrewTestHooks=true | PASS | 0 warnings, 0 errors |
| dotnet restore WinBrew.sln --locked-mode | PASS | after the Microsoft.Extensions.Http lock update |
| dotnet restore --locked-mode -r win-x64 / win-arm64 | N/A | not expressible graph-wide (ADR-0002); CI recipe unchanged |
| dotnet test (Unit + Integration, existing Phase A suites) | PASS | Unit 165/165, Integration 16/16 |
| dotnet publish src/WinBrew.Cli -r win-x64 / win-arm64 | PASS | output == scripts/publish-files.txt (wbrew.exe, e_sqlite3.dll, PDBs) |
| dotnet publish src/WinBrew.Shim (AOT) | NOT RUN | cross-OS native compilation refused — Windows CI |
| Linux CLI smoke (test-hooks, local fixture repo on 127.0.0.1) | PASS | test-ingest; install (exit 10 without consent, 0 with --yes); list/info/--json; dry-run upgrade; upgrade 1.0.0→2.0.0 with retained 1.0.0 and template change (.old rename + cleanup); uninstall; doctor |
| Rejections (same harness) | PASS | zip-slip `..` → 5; ARM32 PE → 5; index v1 after v2 → 5 (rollback); tampered manifest → 5; corrupt artifact → 5; truncated/oversized → 5; http without WINBREW_TEST_MODE → 5; `@v` + `--version` → 2; unknown package → 3; staging and cache clean after each failure |
| Crash harness (WINBREW_FAULT_AT + recover) | PASS | 28/28 fired crash points consistent (install, upgrade, uninstall); 24/24 crash-during-compensation consistent; self install crash → rollback / roll-forward |

## Fault checkpoints covered
| Checkpoint | Test | Result |
|---|---|---|
| download.started … operation.completed (12 lifecycle names) | scratch crash harness: install + upgrade | PASS (all consistent after recover) |
| uninstall.shim.removed, uninstall.package.removed, state.committed, operation.completed | scratch harness: uninstall | PASS |
| compensate.started, compensate.shim.reverted, compensate.package.removed, rollback.completed | scratch harness: crash during recover / during in-command compensation | PASS |
| selfinstall.app.copied, state.committed (self install) | scratch harness | PASS |
| catalog.ingested | — | not yet crash-tested (commit precedes the checkpoint; expected no-op recovery) |
| path.updated | — | Windows only (registry) |
| recovery.failed | — | needs an induced compensation failure (locked/ACL-denied dir) — tests sub-session |

The harness lives outside the repo (scratch); the **committed** integration
tests are the tests sub-session's job.

## Known gaps and deferred items
1. **Tests sub-session (phase acceptance):** malicious-archive corpus (one
   test per `ArchiveDefense`); corrupt/oversized/truncated/interrupted
   downloads against an in-process server (including no/false
   `Content-Length` to exercise the streaming limit); crash at EVERY
   registered checkpoint + `recover` (port the harness: spawn the
   test-hooks CLI, seed via `test-ingest` or in-process
   `IngestIndexUseCase`); the ≥ 35-case quoting corpus by linking
   `src/WinBrew.Shim/ShimRules.cs`; sidecar round-trip
   (`ShimSidecar` → `SidecarReader`); containment cases (self-shim to
   `app\` accepted, others to `app\` refused, `..` refused, `WINBREW_ROOT`
   ignored); PATH via a disposable `HKCU\Software\…` key
   (`RegistryUserPathEditor(subKey)`); "install reads the catalog only"
   (no manifest-to-plan path); redirect/credential policy; `recovery.failed`.
2. **Windows-only (NOT RUN):** shim AOT publish both RIDs (size < 3 MB);
   exit-code / Ctrl+C forwarding and piping through the shim; upgrade while
   the shim runs (no `ERROR_SHARING_VIOLATION`); PATH + `WM_SETTINGCHANGE`;
   Restart Manager names; Credential Manager; the **extraction
   verification test** (fresh profile, no `%TEMP%\.net\`) — result to be
   recorded in ADR-0001.
3. `TestFixtures` has no fixture builder / HTTP server yet (A5) — tests
   sub-session.
4. Expired-metadata auto-refresh (A8), `search`, signed metadata — Phase D.

## Concerns about locked decisions
- None new. Note for D4: the containment walk checks every path component
  for reparse points, so a `%LOCALAPPDATA%` that sits under a junction
  (folder redirection) breaks every shim — consistent with A6's documented
  limitation, but worth stating in known-limitations.md (Phase F).

## Next session must start with
- PROMPT.md + phases/phase-b.md + this checkpoint (+ phase-a-tests.md).
- Build the test-hooks configuration (`-p:WinBrewTestHooks=true`) for the
  integration suite; on Linux, run the CLI framework-dependent — **never
  run `dotnet publish -r …` or a RID-less build in the repo without
  restoring the lock files afterwards** (they get host-/RID-stamped,
  ADR-0002).
