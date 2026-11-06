# Checkpoint: Phase C — 2026-11-06

## Environment
- OS / arch: Linux (Debian 13) x64 — dev host, not Windows 11 (A2).
- .NET SDK (exact): 10.0.403.
- Could execute builds/tests: yes (release and test-hooks builds; real CLI
  child processes; Windows-only tests skip with their reason).

## Sub-session and phase status
- Sub-session: **full** (src + tests in one session; ~14 files, under the
  ~25-file split threshold).
- Phase status: **COMPLETE**.

## Decisions applied / assumptions made
- D2 (PubGrub, determinism, explanations), D8 (dependents, autoremove),
  A9 (qualification, exit 3), A10 (selection order, re-selection,
  manifest-vs-index exact match, cycles, edges, cleanup protection),
  A11 (one journaled operation per plan). ADR-0010 records the design.
- `install` resolves the requested package together with every explicitly
  installed package; when the only solution upgrades an installed package,
  the plan shows it as an `upgrade` item (it is never silent).
- `upgrade <pinned>` and `install <pinned>@<other>` are refused (exit 6,
  D9 "pinned"); a plain `upgrade` keeps pinned packages and whatever
  depends on their pinned versions.
- Manifest verification for the whole plan moved BEFORE the journaled
  operation starts (A10 "exit 5, no changes").
- The JSON install/upgrade payload is now `data.plan.items[]` (one item per
  package: action install/upgrade/keep, version, arch, previous version and
  arch, `architectureTransition`, `architectureFallback`, explicit,
  dependencies) — the Phase B single-package `data.plan` shape is replaced.

## Files changed
| Path | Change | Notes |
|---|---|---|
| src/WinBrew.Core/Resolution/{ResolverModels,VersionSet,PubGrubResolver,GraphOrder}.cs | new | pure resolver, explanations, install order + cycles |
| src/WinBrew.Application/Packages/OperationPlan.cs | new | plan model + `ResolutionPlanner` (snapshot from SQLite, exit 6/3 mapping) |
| src/WinBrew.Application/Packages/PlanExecutor.cs | new | multi-package journaled execution (from the Phase B install path) |
| src/WinBrew.Application/Packages/InstallUseCase.cs | rewritten | install + upgrade use cases, `InstalledLookup` |
| src/WinBrew.Application/Packages/MaintenanceUseCases.cs | new | outdated, pin/unpin, autoremove, cleanup |
| src/WinBrew.Application/Packages/UninstallUseCase.cs | changed | reusable `RemoveAsync` (autoremove) |
| src/WinBrew.Application/Ports/{IStateStores,IPackageFileSystem}.cs | changed | plan commit with edges, pins, edge listing, cache listing |
| src/WinBrew.Infrastructure/Sqlite/SqliteStateStore.cs, Files/ArtifactCache.cs | changed | implementations |
| src/WinBrew.Cli/{PackageCommands,MaintenanceCommands,CommandTree,ServiceConfigurator,Output/EnvelopeModels}.cs | changed/new | upgrade, outdated, pin, unpin, autoremove, cleanup; plan rendering |
| tests/WinBrew.UnitTests/ResolverTests.cs | new | 23 resolver tests |
| tests/WinBrew.IntegrationTests/DependencyCliTests.cs | new | 14 end-to-end dependency/maintenance tests |
| tests/WinBrew.IntegrationTests/{CrashRecoveryTests,LifecycleCliTests,CliHarness}.cs | changed | multi-package upgrade crash scenario; plan JSON shape |
| docs/adr/0010-…md, this file, README, CHANGELOG | new/changed | |

## Build and test results (actual output only)
| Command | Result | Evidence |
|---|---|---|
| dotnet build -c Release (release + test-hooks) | PASS | 0 warnings, 0 errors |
| dotnet restore --locked-mode | PASS | no package changes this phase |
| dotnet test (test-hooks build) | PASS | Unit **524/524**; Integration **167 passed, 14 skipped** (Windows-only) |
| dotnet test (release build) | PASS | Unit 524/524; Integration 72 passed, 58 skipped (test-hooks-only + Windows-only) |
| docker build --target test | PASS | release pass 524 + 72/58 skipped; test-hooks pass 524 + 167/14 skipped |

## Phase C acceptance (phase-c.md)
| Item | Evidence | Status |
|---|---|---|
| Classic PubGrub conflicts, diamonds, pinned conflicts, cycles, architecture-unavailable | ResolverTests (NoConflicts, AvoidingConflict…, PerformingConflictResolution, PartialSatisfier, Linear/Branching error reporting, Diamond, PinnedConflict, Cycle, ArchitectureUnavailable×2) | PASS |
| Same plan across 100 randomized metadata orderings | SamePlan_Across100RandomizedMetadataOrderings (+ same explanation) | PASS |
| Same plan with/without another repository holding same-named packages | resolver unit test + DependencyCliTests.SamePlan_… | PASS |
| Resolver performs no I/O | TheResolver_LivesInCore_WhichHasNoIoDependencies (construction) | PASS |
| Manifest dependencies/architectures disagreeing with the catalog → exit 5, no change | DependencyCliTests.ManifestDependencies… (snapshot), LifecycleCliTests.Install_ManifestThatDiffers… (architectures) | PASS |
| A conflict causes zero filesystem/state changes | DependencyCliTests.Conflict_… (snapshot comparison of the whole root; no HTTP request) | PASS |
| A failed upgrade at every checkpoint leaves the previous version usable | CrashRecoveryTests MultiPackageUpgrade × 12 checkpoints (+ the Phase B single-package upgrade) | PASS |
| Cleanup never removes active, pinned, referenced or retained artifacts | DependencyCliTests.Cleanup_… (+ refuses entirely when protection is unknown) | PASS |

## Fault checkpoints covered
No new checkpoints. The multi-package upgrade scenario adds 12 crash cases
to the existing registry coverage (`CoverageTable_CoversEveryRegisteredCheckpoint_Exactly`
still matches the registry exactly). `path.updated` remains Windows-only.

## Known gaps and deferred items
1. Lockfiles recording the re-selected architecture: Phase D (bundles).
2. Expired-metadata auto-refresh before planning (A8): Phase D.
3. The Phase B Windows-only items (AOT shim process, PATH hive, extraction
   check) still need Windows CI (Phase F).
4. Disk-full fault injection (exit 12) still has no IOException seam.

## Concerns about locked decisions
- None. Note for D2 rule 2: "prefer already-installed versions" is a
  preference, so an install can upgrade an installed package when that is
  the only solution — shown in the plan and approved with it.

## Next session must start with
- PROMPT.md + `phases/phase-d.md` + this checkpoint.
- Phase D: simplified-TUF trust (ECDSA P-256 envelopes, root rotation,
  timestamp/index), `repo add/list/remove`, `update`, `search` (FTS5,
  migration v2), bundles + lockfiles, `self update` / `self uninstall`.
  The final ingest path (`IngestIndexUseCase`) is already in place behind
  `IMetadataVerifier`.
