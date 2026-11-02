# WinBrew Implementation Prompt v2.3 — Phase D (Trusted repositories and bundles)

> **Read `PROMPT.md` first** — it is sent in the same session and
> contains Part A (the design you must implement) and Part C (the
> checkpoint templates you must fill). This file adds only what is
> specific to this phase. `CHANGES.md` is reference material for the
> project owner and is never sent to implementation sessions.

Each phase session must:
1. Start by restating which decisions from Part A it touches, and any
   assumptions it makes.
2. Implement only its scope, in buildable steps.
3. **Context-window management:** **Phases A and B split into `src` and
   `tests` sub-sessions by default** (this is the plan, not an
   exception); any other phase splits the same way if it requires more
   than ~25 files or approaches context limits. In every split,
   complete all production code (`src/*` and `schemas/*`) first, verify
   `dotnet build` passes, and emit a mid-phase checkpoint report so
   `tests/*` can be completed in the immediate continuation session
   without truncating files. A mid-phase checkpoint is marked
   **INCOMPLETE** (Part C1); **a phase counts as complete only after
   its test sub-session passes and the final checkpoint says
   COMPLETE.**
4. Run the available builds and tests and report the real output.
5. End with a checkpoint report (Part C1).

## Phase D — Trusted repositories and bundles

**Scope:** D6 trust implementation (envelope; SPKI parsing with
`System.Formats.Asn1` before import; algorithm-OID, named-curve,
uncompressed-point, `KeySize==256` and 64-byte-signature rules;
duplicate-key-ID rejection; threshold counting; roles; rotation walk;
version persistence; expiry; clock skew); the real
`IMetadataVerifier` implementation replacing `TestHooksUnsignedVerifier`
(a release build refuses an unsigned or partially signed index with
exit 5; a reflection test asserts `TestHooksUnsignedVerifier` does not
exist in the release assembly); the **sparse-index catalog**:
`index.json` version entries with `sha256`, `size`,
`architectures[]`, `dependencies[]` (no `latestVersion`), ingested into
SQLite together with the accepted role versions **in one transaction**;
`trust.maxMetadataBytes` enforced from `timestamp.json`'s `length`
while streaming; search per the D6 semantics (FTS5 or `search_text`
matching per the Phase A probe; `search_text` built with
`ToLowerInvariant()` at ingest, queries lowercased the same way);
`repo add` with fingerprint approval; `repo list` and
`repo remove`; `update` (all three take the mutation lock);
`self update` against a signed fixture feed (D6 root chain, initial
compiled-in root, N→N+1 rotation); a fixture repository generator
(`scripts/new-fixture-repo.ps1` plus the `WinBrew.TestFixtures`
signer); bundle export, install and `--locked`; `--offline` (expired
metadata only if verified while valid, with age shown).

**Acceptance**
- Negative tests: tampered payload, wrong key, wrong ECDSA curve,
  explicit curve parameters, non-uncompressed point, wrong signature
  length, duplicate key IDs, below threshold, expired role, rolled-back
  version, replayed old timestamp, freeze attack, rotation without
  cross-signing, oversized metadata, and a manifest digest mismatch.
- A release build refuses an unsigned or partially signed index with
  exit 5, and a reflection test confirms `TestHooksUnsignedVerifier`
  does not exist in the release assembly.
- A manifest whose `dependencies` or `architectures` disagree with the
  signed index entry fails with exit 5 and changes nothing.
- Catalog rows and role high-water marks are written atomically
  (one transaction) — verified by a fault-injected crash mid-ingest.
- A locked restore reproduces exact versions and digests. A missing
  entry fails explicitly.
- An offline locked restore from cache succeeds; offline use of expired
  metadata warns with its age; never-valid metadata is refused offline.
- `repo add` in non-interactive mode without `--trust-root` exits with 10.
- `wbrew search` against a generated 10,000-package fixture catalog
  completes in under 1 second — **measured on the CI runner, median of
  10 runs** (A17).
