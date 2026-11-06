# Checkpoint: Phase D (src sub-session) — 2026-11-06

## Environment
- OS / arch: Linux (Debian 13) x64 — dev host, not Windows 11 (A2).
- .NET SDK (exact): 10.0.403.
- Could execute builds/tests: yes (builds; the test sub-session follows in
  the same session).

## Sub-session and phase status
- Sub-session: **src** (production code + scripts; ~30 files).
- Phase status: **INCOMPLETE** — the tests sub-session must pass before
  Phase D counts as complete.

## Decisions applied / assumptions made
- D6 trust in `WinBrew.Core/Trust` (pure, no I/O): SPKI parsed with
  `System.Formats.Asn1` BEFORE import (id-ecPublicKey, named curve P-256 only,
  uncompressed 65-byte point), `KeySize == 256` after import, signatures
  exactly 64 bytes with IEEE P1363 always explicit, duplicate key IDs
  rejected, each distinct key counted once, thresholds per role, rotation
  N→N+1 signed by a threshold of root N AND of root N+1, high-water marks,
  expiry with `trust.maxClockSkewSeconds`, maximum lifetimes (root 365 d,
  timestamp 7 d, index 30 d).
- `System.Formats.Asn1` is used from the shared framework (in-box since
  .NET 5); the ADR-0002 pin stays unreferenced, so no lock file changes.
- The build-conditional verifiers are gone: `TestHooksUnsignedVerifier`,
  `UnavailableMetadataVerifier`, `IMetadataVerifier` and the hidden
  `test-ingest` command were deleted. Every build (release and test-hooks)
  now accepts metadata ONLY through the signed path
  (`RepositoryMetadataService`).
- `repo add --trust-root`: every given fingerprint must be a root-role key
  of the downloaded root, and together they must reach the root threshold;
  otherwise exit 5. No fingerprint + non-interactive → exit 10.
- `repo remove` refuses (exit 6) while packages from that repository are
  installed.
- Search: FTS5 trigram index (`case_sensitive 1`) over the lowercased
  `search_text` for queries ≥ 3 characters (GLOB, metacharacters escaped),
  `instr()` for shorter queries; never LIKE (schema migration v2).
- Lockfile default path: `dev.json` → `dev.lock.json` beside the bundle.
- Self update: feed = same layout and roles as a repository; the index
  payload type is `application/vnd.winbrew.releases+json;version=1`. The feed
  URL and initial root come from `-p:WinBrewReleaseFeedUrl` /
  `-p:WinBrewReleaseRoot` (empty by default → exit 3); test-hooks builds with
  `WINBREW_TEST_MODE=1` may use `WINBREW_TEST_RELEASE_FEED` /
  `WINBREW_TEST_RELEASE_ROOT`.
- Test-hooks builds with `WINBREW_TEST_MODE=1` honour `WINBREW_TEST_NOW`
  (shifted clock) for expiry/offline-age tests; compiled out of release.

## Files changed
| Path | Change | Notes |
|---|---|---|
| src/WinBrew.Core/Trust/{SpkiRules,TrustMetadata,TrustChain}.cs | new | D6 rules, parsers, chain |
| src/WinBrew.Core/Release/ReleaseIndex.cs, Bundles/BundleModels.cs | new | A14 feed index, A15 bundle/lockfile |
| src/WinBrew.Core/Operations/FaultCheckpoints.cs | changed | `catalog.rows.written` (inside the transaction), `selfupdate.app.copied` |
| src/WinBrew.Application/Catalog/{RepositoryMetadataService,RepositoryUseCases}.cs | new | verified refresh, freshness (A8/A15), repo add/list/remove, update, search |
| src/WinBrew.Application/Bundles/BundleUseCases.cs | new | export, install, --locked |
| src/WinBrew.Application/SelfManagement/{SelfUpdate,SelfUninstall}UseCase.cs | new | A14 |
| src/WinBrew.Application/{Packages/*,Lifecycle/*,Ports/*} | changed | freshness in install/upgrade/info/outdated, lock pins, settings skew, recovery for self update |
| src/WinBrew.Infrastructure/Sqlite/{Migrations,SqliteStateStore,SqliteStateDatabase}.cs | changed | migration v2, atomic metadata commit, search, self-update trust |
| src/WinBrew.Cli/{TrustCommands,BuildConfiguration}.cs | new | commands, feed config, test clock |
| src/WinBrew.Cli/{CommandTree,PackageCommands,ServiceConfigurator,Output/EnvelopeModels}.cs, WinBrew.Cli.csproj | changed | |
| scripts/new-fixture-repo.ps1 | new | signed fixture generator |
| (deleted) Application/Catalog/IngestIndexUseCase.cs, Infrastructure/Trust/MetadataVerifiers.cs | removed | replaced by the signed path |

## Build and test results (actual output only)
| Command | Result | Evidence |
|---|---|---|
| dotnet build src/WinBrew.Cli -c Release | PASS | 0 errors |
| dotnet build src/WinBrew.Cli -c Release -p:WinBrewTestHooks=true | PASS | 0 errors |
| wbrew recover (fresh root, test-hooks build) | PASS | migration v2 incl. FTS5 trigram table applied |
| dotnet build WinBrew.sln | FAIL (expected) | tests not yet updated (`FakeStateDatabase`, `test-ingest`) |
| scripts/new-fixture-repo.ps1 | NOT RUN | no PowerShell on the Linux dev host |

## Next sub-session must start with
- Update the test fixtures (signer, signed `FixtureRepository`), the harness
  (`repo add --trust-root`), the crash coverage table (`catalog.rows.written`,
  `selfupdate.app.copied`), then the Phase D acceptance tests.
