# ADR-0003: Storage schema v1, migrations, backups (A6)

**Status:** accepted (Phase A src sub-session, 2026-11-05)

## Decision

- SQLite via Microsoft.Data.Sqlite with the bundled `SQLitePCLRaw.bundle_e_sqlite3`
  provider; `Batteries_V2.Init()` runs before first use (A4).
- WAL mode; `PRAGMA foreign_keys=ON` per connection; `synchronous=NORMAL` on
  write connections (WAL pairs with NORMAL safely).
- Schema versioning through `PRAGMA user_version`; forward-only migration list
  (`Migrations.All`); **never downgrade** — a database newer than the binary
  produces a clear `WinBrewException` mapped to exit 1 (A6).
- **Before every migration step**, the database is copied via the SQLite
  backup API (`SqliteConnection.BackupDatabase`) into
  `state\backups\winbrew-pre-v<N>-<UTC timestamp>.db`.
- Schema v1 DDL is adopted verbatim from the approved Phase 0 plan (PLAN §5):
  repositories, role_versions, catalog_packages/versions/architectures/
  dependencies, installed_packages, dependency_edges, pins, shims,
  operations, operation_steps, journal_files — including the partial index
  `idx_ops_open` and `idx_files_pending`.

## Evidence (observed, not assumed)

Probe against the real `SqliteStateDatabase` (Phase A probe harness,
2026-11-05): empty file → `user_version` -1 → `ApplyMigrationsAsync` →
`user_version` 1; exactly one backup file created
(`winbrew-pre-v1-20261105-065055.db`); FTS5 probe against the same bundle:
**available** (see ADR-0005).

## Consequences

- Migration v2 (FTS5 search index, if adopted in Phase D) and the Phase E
  MSI tracking migration (v3) follow the same forward-only pattern.
- Read-only commands open the database read-only and never create or migrate
  it; doctor reports "no database yet" honestly.
- The sqlite3 native binary is `e_sqlite3.dll` on Windows (ships next to
  `wbrew.exe`, ADR-0001); on Linux the bundle resolves the platform-native
  copy for development/test runs.
