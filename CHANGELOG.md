# Changelog

All notable changes to WinBrew are documented here.
This project follows [Keep a Changelog](https://keepachangelog.com/)
semantics; versions are `NuGetVersion`-normalized (D3).

## [Unreleased]

## [0.1.0] — 2026-11-07 (first preview, UNSIGNED)

Preview build for trying WinBrew end to end. Not for production use:
binaries are not Authenticode-signed, and no public repository or
self-update feed exists yet (`docs/release-readiness.md`).

### Added (deployment)
- `deploy/server/`: Docker Compose stack serving a signed demo repository
  over HTTPS (Caddy, with Let's Encrypt or a local CA) and `winbrew-repo`
  (`tools/WinBrew.RepoTool`), which packs package folders into deterministic
  ZIPs, signs root/timestamp/index with persistent keys and re-signs daily.
  The demo packages (`hello` 1.0.0/2.0.0, `toolbox`, `echoargs`,
  `exitcode`) are real Windows x64 programs cross-compiled with MinGW-w64.
- `deploy/client/Install-WinBrew.ps1`: verifies a release zip against
  `SHA256SUMS.txt`, self-installs, optionally trusts the server's CA, and
  adds the repository. Walkthrough: `deploy/README.md`.
- `docs/user-manual.md` and architecture diagrams in the README.

### Fixed (found by the first Windows CI runs)
- Per-machine MSI/EXE operations always failed: helper results with null
  members were rejected (exit 5).
- Redirected stdout/stderr (pipes, `--json`) were encoded in the OEM code
  page on Windows instead of UTF-8.
- A corrupt download kept the artifact file locked on Windows (ZIP stream
  leak).

### Added (Phase F — documentation and release hardening, 2026-11-06)
- Documentation: architecture, CLI reference (full exit-code table incl. 13
  and the 130-vs-0xC000013A note), manifest and repository/trust
  specifications, package-author guide (`persist` exclusion,
  `writesToInstallDir`), security model, STRIDE threat model, recovery and
  troubleshooting, known limitations, release checklist, release-readiness
  report; ADRs 0013–0021 and an ADR index.
- `ci.yml` (Windows x64 + ARM64, locked per-RID restore, release and
  test-hooks test passes, packaging, release-binary checks; Linux Docker) and
  `release.yml` (publish → sign in the protected `release-signing`
  environment or label UNSIGNED → zips, SHA256SUMS.txt, CycloneDX SBOM,
  signature verification, draft release).
- `scripts/package-release.ps1`, `scripts/wbrew-completion.ps1`;
  machine-readable release-zip set in `scripts/publish-files.txt`;
  release-artifact tests (`tests/WinBrew.EndToEndTests`).
- Versioned example bundle and lockfile (`examples/bundles/`).

### Changed
- `self install` / `self update` also copy `WinBrew.ElevatedHelper.exe` when
  it ships next to `wbrew.exe`.

### Added (Phase E — v1.1, 2026-11-06; Windows-runner checks pending)
- MSI and EXE installer packages: typed `installer` declarations, explicit
  consent (`--allow-installer`), `--scope machine`, ProductCode tracking,
  context confirmation, EXE detection rules (ambiguous → exit 8),
  msiexec exit-code mapping (1618 retried, then exit 13 `InstallerBusy`).
- `WinBrew.ElevatedHelper.exe`: typed request file only; refuses untrusted
  `%ProgramData%\WinBrew` directories (owner + DACL + reparse checks on
  handles), creates its directories with their final protected descriptor,
  runs only a hash-verified copy held open with `FileShare.Read`.
- Reconciliation in `list` and `doctor` (drift reported, never corrected);
  `self uninstall` refuses while installer-managed packages remain.
- Schema migration v3 (`installer_packages`); WiX fixture MSI and fixture
  EXE sources with `scripts/build-installer-fixtures.ps1`.

### Added (Phase D — complete, 2026-11-06)
- Simplified-TUF repository trust (D6): ECDSA P-256 envelopes over exact
  payload bytes, SPKI rules checked before import, thresholds, root rotation
  N→N+1 (cross-signed), timestamp/index binding, replay/rollback/freeze
  protection, expiry with clock skew, maximum lifetimes.
- `repo add` (fingerprint approval; `--trust-root`, exit 10 without it when
  non-interactive), `repo list`, `repo remove`, `update`, `search` (FTS5
  trigram index, schema migration v2).
- Expired metadata: read-only commands warn, `--dry-run` refuses, online
  installs refresh automatically, `--offline` warns with its age (A8/A15).
- `bundle export`, `bundle install [--locked]` with lockfiles (exact
  versions, architectures, repository identity, manifest and artifact
  digests); offline locked restore from the verified cache.
- `self update` from a signed release feed (same trust model; exits 3 when
  no feed is configured in the build) and `self uninstall [--keep-data]`.
- `scripts/new-fixture-repo.ps1` and the `RepositorySigner` test fixture.

### Removed
- The unsigned Phase B ingest path (`test-ingest`, `TestHooksUnsignedVerifier`):
  every build accepts only signed metadata.

### Added (Phase C — complete, 2026-11-06)
- PubGrub dependency resolver (pure, deterministic, explanations naming
  repository-qualified ids and original ranges; cycles refused with their
  path; architecture fallback and re-selection, A10).
- Multi-package plans as ONE journaled operation: all-or-nothing up to
  StateCommitted; dependency edges and explicit/dependency tracking.
- `upgrade`, `outdated`, `pin`, `unpin`, `autoremove`, `cleanup` (never
  removes active, pinned, referenced or retained artifacts).

### Changed
- `install`/`upgrade` JSON: `data.plan.items[]` (one item per package).

### Added (Phase B — complete, 2026-11-06)
- Portable lifecycle: `install` (single packages without dependencies;
  installing another version journals an upgrade), `uninstall`, `list`,
  `info`, `recover`, `self install` (zip layout).
- Journal + recovery with 22 registered fault checkpoints; roll-forward
  after `StateCommitted`, compensation before it, `RecoveryFailed` → exit 8.
- A13 HTTP client (manual redirects, credential origin policy, retries,
  size limits on actual bytes, streaming SHA-256, redaction), content-
  addressed cache, ZIP extraction with every archive defense.
- Native AOT shim: MSVC argv[0] rule, 32,767-char limit (206), sidecar
  containment (9009), job object, Ctrl+C handler routine, GUI targets.
- PATH editor (HKCU), Restart Manager and Credential Manager adapters.
- Final catalog ingest path; unsigned metadata only in test-hooks builds.
- Tests: shim quoting corpus, A13 malicious-archive corpus, HTTP client
  policy, crash-at-every-checkpoint recovery, lifecycle and self-install
  suites (real CLI child processes via `tests/WinBrew.CliTestHost`).

### Fixed
- A manifest without the optional `dependencies`/`executables` arrays no
  longer crashes validation (absent now means empty).

### Added (Phase A — complete, 2026-11-05)
- Solution scaffolding per the architecture tree: `WinBrew.Core`,
  `WinBrew.Application`, `WinBrew.Infrastructure`, `WinBrew.Windows`,
  `WinBrew.Cli`, `WinBrew.Shim` (Native AOT project; Phase B implements
  its full behavior), and the four test projects.
- JSON Schemas v1: manifest, repository root, repository timestamp,
  repository index, bundle, lockfile, config, CLI output.
- Config store (D10 keys, defaults, validation), SQLite state database
  (WAL, `user_version` migrations, backup-before-migrate, schema v1
  including journal tables), mutation lock (D5), `doctor`, `config`
  commands, fast-path `--version`/`--help`, exit-code mapping (D9),
  JSON output envelope, consent/non-interactive model, the FTS5 probe.
