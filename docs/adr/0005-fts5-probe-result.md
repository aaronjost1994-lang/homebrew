# ADR-0005: FTS5 probe result and the D6 search decision

**Status:** accepted (Phase A src sub-session, 2026-11-05)

## Probe (phase-a.md deliverable)

`SqliteStateDatabase.SupportsFts5Async` executes
`CREATE VIRTUAL TABLE temp.fts5probe USING fts5(content)` against the same
provider bundle that ships with wbrew (PROBES.md §4 explicitly warns the
result must come from the pinned bundle, not any other build).

**Observed 2026-11-05:** Microsoft.Data.Sqlite **10.0.14** with
SQLitePCLRaw.bundle_e_sqlite3 **2.1.13** — `fts5=True` (FTS5 module present).

## Decision (PLAN §2 item 2)

- **FTS5 is available and is adopted** for `wbrew search` once the catalog
  lands (Phase D): the search index will be an FTS5 table over the
  `search_text` column, shipped as **schema migration v2** (forward-only,
  A6), built during ingest in the same transaction as the catalog rows
  (D6).
- The D6 query-lowering rule stays: `search_text` is built with
  `ToLowerInvariant()` at ingest; queries are lowercased the same way; LIKE
  or FTS5 MATCH must never be relied on for case folding.
- `doctor` surfaces the probe result (`sqlite.fts5` check) so the decision
  is visible at runtime.
- The probe re-runs in CI against the shipped bundle; a regression (bundle
  change without FTS5) would be caught by the migration test for v2.

## Consequences

- Search latency target (A17: 10k-package catalog, median of 10, < 1 s) is
  measured against the FTS5 path in the tests sub-sessions.
- If a future bundle regresses to FTS5-less SQLite, migration v2's absence
  must degrade search to direct matching with a doctor warning — handled in
  Phase D, not silently.
