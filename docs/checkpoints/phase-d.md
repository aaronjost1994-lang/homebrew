# Checkpoint: Phase D — 2026-11-06

## Environment
- OS / arch: Linux (Debian 13) x64 — dev host, not Windows 11 (A2).
- .NET SDK (exact): 10.0.403.
- Could execute builds/tests: yes (release and test-hooks builds; real CLI
  child processes; Windows-only tests skip with their reason).

## Sub-session and phase status
- Sub-sessions: **src** (`phase-d-src.md`, INCOMPLETE) then **tests**, both in
  this session.
- Phase status: **COMPLETE** (on the Linux dev host; CI-only items below are
  NOT RUN).

## Decisions applied / assumptions made
- D6, A8 (freshness, consent), A9 (`writesToInstallDir` from the signed
  manifest), A13 negative cases, A14, A15, A17 (search). ADR-0011 records
  the design; `phase-d-src.md` lists the assumptions (trust-root threshold
  rule, `repo remove` refusal, lock path, feed layout, test-only seams).
- Tests sub-session additions: the test signer serializes envelopes with
  relaxed escaping so base64 `+`/`/` stay literal (stable lengths);
  `self update` reports "already installed" when the wbrew shim already
  starts the chosen release.

## Files changed (tests sub-session)
| Path | Change | Notes |
|---|---|---|
| tests/WinBrew.TestFixtures/RepositorySigner.cs | new | keys, envelopes, roots + rotation, timestamp, index |
| tests/WinBrew.TestFixtures/{FixtureRepository,Fakes}.cs | changed | signed publishing, `EditIndex`, `DeleteDatabaseAsync` |
| tests/WinBrew.UnitTests/TrustChainTests.cs | new | 23 tests: 19 D6 rule tests (incl. the reflection test) + 4 bundle/lockfile/release-index tests |
| tests/WinBrew.IntegrationTests/{TrustCliTests,BundleCliTests,SelfUpdateTests}.cs | new | Phase D acceptance end to end |
| tests/WinBrew.IntegrationTests/{CliHarness,CrashRecoveryTests,LifecycleCliTests,DependencyCliTests,MigrationTests}.cs | changed | `repo add --trust-root`, Update + SelfUpdate crash scenarios, v2 migration |
| src/WinBrew.Application/SelfManagement/SelfUpdateUseCase.cs | changed | already-active check |
| docs/adr/0011-…md, this file, README, CHANGELOG | new/changed | |

## Build and test results (actual output only)
| Command | Result | Evidence |
|---|---|---|
| dotnet build -c Release (release + test-hooks) | PASS | 0 warnings, 0 errors |
| dotnet restore --locked-mode | PASS | no package or lock file changes |
| dotnet test (test-hooks build) | PASS | Unit **547/547**; Integration **215 passed, 14 skipped** (Windows-only) |
| dotnet test (release build) | PASS | Unit 547/547; Integration 74 passed, 80 skipped (test-hooks-only + Windows-only) |
| docker build --target test | PASS | release pass 547 + 74/80 skipped; test-hooks pass 547 + 215/14 skipped |
| scripts/new-fixture-repo.ps1 | NOT RUN | no PowerShell on the dev host (Windows CI, Phase F) |

## Phase D acceptance (phase-d.md)
| Item | Evidence | Status |
|---|---|---|
| Tampered payload, wrong key, wrong curve, explicit parameters, non-uncompressed point, wrong signature length, duplicate key IDs, below threshold | TrustChainTests (Spki_*, Signature_*, DuplicateKeyIds_*, Root_BelowThreshold, PartiallySignedIndex_*, TamperedPayload_*) + TrustCliTests.Update_RejectsAttack (TamperedPayload, WrongKey, NoSignatures, DuplicateKeyIds, WrongSignatureLength, WrongPayloadType) | PASS |
| Expired role, rolled-back version, replayed old timestamp, freeze attack | TrustChainTests.Timestamp_*/Index_*; TrustCliTests (RolledBackIndex, ReplayedOldTimestamp, FreezeAttack, RepoAdd_ExpiredTimestamp…), LifecycleCliTests.IndexRollback_… | PASS |
| Rotation without cross-signing | TrustChainTests.Rotation_*, TrustCliTests.Update_RotationWithoutCrossSigning, SelfUpdateTests.…WithoutCrossSigning | PASS |
| Oversized metadata | TrustCliTests.OversizedMetadata_… (declared length above trust.maxMetadataBytes), Attack.IndexLongerThanDeclared (streaming) | PASS |
| Manifest digest mismatch | TrustCliTests.ManifestDigestMismatch_… | PASS |
| Release build refuses unsigned / partially signed index (exit 5) | No build-conditional verifier exists any more; TrustChainTests (unsigned, below index threshold 2) run in the release build; TrustCliTests NoSignatures (test-hooks) | PASS |
| `TestHooksUnsignedVerifier` absent from the release assembly (reflection) | TrustChainTests.TestHooksUnsignedVerifier_DoesNotExistInTheInfrastructureAssembly (release run) | PASS |
| Manifest deps/architectures ≠ signed index entry → exit 5, no change | DependencyCliTests.ManifestDependencies…, LifecycleCliTests.Install_ManifestThatDiffers… (re-signed index) | PASS |
| Catalog rows + role marks in one transaction (crash mid-ingest) | CrashRecoveryTests Update × {catalog.rows.written (inside the transaction → old catalog AND old marks), catalog.ingested} | PASS |
| Locked restore reproduces exact versions and digests; missing entry fails explicitly | BundleCliTests.Export_…, LockedRestore_ReproducesExactVersions…, LockedRestore_MissingOrDifferentEntry × 5 | PASS |
| Offline locked restore from cache; expired metadata offline warns with age; never-valid refused offline | BundleCliTests.OfflineLockedRestore_…, TrustCliTests.ExpiredMetadata_…, RepoAdd_ExpiredTimestamp_…NeverValid… | PASS |
| `repo add` non-interactive without `--trust-root` → 10 | TrustCliTests.RepoAdd_NonInteractive_WithoutTrustRoot_Exits10 | PASS |
| `wbrew search` on a generated 10,000-package catalog < 1 s, median of 10, **CI runner** | Dev host (Linux, framework-dependent test host, whole CLI process): **median 463 ms** (min 398, max 549). CI runner measurement: NOT RUN (Phase F) | PASS (dev host) / NOT RUN (CI) |
| self update vs signed feed: initial root, N→N+1 rotation; self uninstall | SelfUpdateTests (4) + CrashRecoveryTests SelfUpdate × 9 checkpoints | PASS |

## Fault checkpoints covered
Registry now 24 names: + `catalog.rows.written`, `selfupdate.app.copied`.
`CoverageTable_CoversEveryRegisteredCheckpoint_Exactly` passes; new
scenarios Update (2 checkpoints) and SelfUpdate (9). `path.updated` remains
Windows-only.

## Known gaps and deferred items
1. A17 search target measured on the CI runner, and the PowerShell fixture
   generator: Phase F (Windows CI).
2. The previous `app\<ver>\` is kept but not pruned "at the next successful
   start" (A14); pruning is not implemented yet.
3. `self uninstall`'s installer-managed refusal is trivially satisfied in v1;
   Phase E completes it. It is idempotent, not journaled.
4. The official release feed URL and initial root are empty until release
   infrastructure exists (`self update` exits 3).
5. Disk-full fault injection (exit 12) still has no IOException seam.

## Concerns about locked decisions
- None. Note: `--trust-root` accepts a subset of root keys that reaches the
  root threshold (rather than requiring every root key); ADR-0011 records it.

## Next session must start with
- PROMPT.md + `phases/phase-e.md` + this checkpoint.
