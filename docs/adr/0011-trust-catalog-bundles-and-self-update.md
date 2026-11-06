# ADR-0011: Repository trust, sparse-index catalog, bundles, self update (D6, A8, A14, A15)

**Status:** accepted (Phase D, 2026-11-06)

## Decisions

1. **One verified path, in every build.** `WinBrew.Core/Trust` implements D6
   with no I/O: `SpkiRules` (SPKI parsed with `System.Formats.Asn1` before any
   import — id-ecPublicKey, parameters exactly the named-curve OID P-256,
   uncompressed 65-byte point; `KeySize == 256` after import; signatures exactly
   64 bytes, `IeeeP1363FixedFieldConcatenation` always explicit),
   `TrustMetadataParser` (strict envelope/root/timestamp parsing; key ID must
   equal SHA-256 of its SPKI; duplicate key IDs in a role rejected) and
   `TrustChain` (thresholds over DISTINCT valid keys of the role; duplicate key
   IDs in an envelope rejected; bootstrap; N→N+1 rotation signed by a threshold
   of root N AND of root N+1; timestamp; index bound by length and SHA-256;
   high-water marks; expiry with `trust.maxClockSkewSeconds`; maximum lifetimes
   365/7/30 days). The Phase B `IMetadataVerifier`,
   `TestHooksUnsignedVerifier`, `UnavailableMetadataVerifier` and the hidden
   `test-ingest` command are deleted: release AND test-hooks builds accept
   metadata only through `RepositoryMetadataService`. Integration tests sign
   their fixtures (`WinBrew.TestFixtures.RepositorySigner`).
2. **Verification order and atomicity.** root (walk `root/<N+1>.json` until
   HTTP 404, at most 1024 steps) → timestamp → index (fetched with
   `ExpectedSize = timestamp length` and `MaxBytes = trust.maxMetadataBytes`,
   so a longer body is rejected while streaming; a declared length above the
   limit is rejected before fetching) → ONE SQLite transaction writing the
   repository row, the accepted root envelope, the root/timestamp/index
   high-water marks, expiries and every catalog + search row. The fault
   checkpoint `catalog.rows.written` fires INSIDE that transaction, so the
   crash test proves atomicity; `catalog.ingested` fires after COMMIT. An
   unchanged timestamp version means "nothing new" (the accepted index must
   still be valid).
3. **Bootstrap consent.** `repo add` shows the root-role fingerprints (key
   IDs). `--trust-root` values must each be a root-role key and together reach
   the root threshold, else exit 5; no `--trust-root` in a non-interactive
   session exits 10 (`--yes` never implies trust). `repo remove` refuses while
   packages from the repository are installed (exit 6).
4. **Freshness (A8/A15)** lives in `MetadataFreshness`: read-only commands
   (`search`, `info`, `outdated`) warn about expired-but-once-valid metadata;
   `--dry-run` refuses it (exit 5, run `wbrew update`); online
   `install`/`upgrade`/`bundle install` refresh it through the normal path and
   put "Repository metadata refreshed: … expired N days ago" at the top of the
   plan; `--offline` uses it with a warning that includes its age and the time
   it was verified. Only metadata that passed full verification is ever
   stored, so "never valid" metadata cannot be used online or offline.
5. **Search** (ADR-0005 follow-up): schema migration v2 adds an FTS5 table
   `catalog_search` with the trigram tokenizer and `case_sensitive 1` over the
   same `search_text` (built with `ToLowerInvariant()`); queries are lowercased
   the same way. Queries of ≥ 3 characters use `GLOB '*q*'` (metacharacters
   escaped as classes) on the trigram index; shorter queries use
   `instr(search_text, q)`. LIKE is never used. Results are ordered by id.
6. **Bundles.** `bundle export` writes the explicit packages (with pins) and
   their repositories, and a lockfile of every installed package from the
   verified cached manifests. `bundle install` requires the bundle's
   repositories to be configured with the same URL (never adds trust),
   resolves the requested packages as one plan, then writes the lockfile.
   `--locked` never resolves: the lockfile's repositories must match by URL and
   root fingerprints (exit 5), every requested package and every catalog
   dependency must have an entry (exit 3), the lockfile's manifest digest must
   equal the signed index entry and the manifest's artifact URL and SHA-256
   must equal the lockfile (exit 5) — all checked before the journaled
   operation starts. Default lock path: `dev.json` → `dev.lock.json`.
7. **Self update.** The feed has the repository layout (root.json,
   root/<N>.json, timestamp.json, index.json) with payload type
   `application/vnd.winbrew.releases+json;version=1`. The initial root and URL
   are compiled in (`-p:WinBrewReleaseFeedUrl`, `-p:WinBrewReleaseRoot`); both
   are empty today, so `self update` exits 3 ("self-update not configured for
   this build"). Accepted feed versions live in `self_update_trust`. The update
   is one journaled `selfupdate` operation: download + SHA-256 → extract (A13)
   → `app\<ver>\` via temp dir + rename (`selfupdate.app.copied`) → the wbrew
   sidecar → every shim binary replaced by the new template → one transaction
   recording the template digest on every shim row (StateCommitted). The
   running binary is never overwritten and the previous `app\<ver>\` stays.
8. **Self uninstall** removes the managed PATH entry and every shim, and
   unless `--keep-data` the packages, cache, staging, logs, config and the
   database (connection pools are cleared first); it reports the running
   app directory and the root for manual removal. It is idempotent rather
   than journaled: re-running finishes an interrupted uninstall.
9. **Test-only seams** (test-hooks build + `WINBREW_TEST_MODE=1`, compiled
   out of release): `WINBREW_TEST_NOW` shifts the clock;
   `WINBREW_TEST_RELEASE_FEED` / `WINBREW_TEST_RELEASE_ROOT` name a fixture
   feed and its initial root file.

## Consequences

- `System.Formats.Asn1` comes from the shared framework; the ADR-0002 pin is
  unused and lock files are unchanged.
- The previous app version is not pruned at the next start (A14 "kept until
  the next successful start"); it stays until a later phase adds pruning.
- The installer-managed refusal in `self uninstall` is trivially satisfied in
  v1 (schema v1 allows only portable packages) and is completed in Phase E.
