# Changelog

All notable changes to WinBrew are documented here.
This project follows [Keep a Changelog](https://keepachangelog.com/)
semantics; versions are `NuGetVersion`-normalized (D3).

## [Unreleased]

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
