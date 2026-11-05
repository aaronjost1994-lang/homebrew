# Changelog

All notable changes to WinBrew are documented here.
This project follows [Keep a Changelog](https://keepachangelog.com/)
semantics; versions are `NuGetVersion`-normalized (D3).

## [Unreleased]

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
