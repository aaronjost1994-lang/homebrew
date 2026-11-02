# WinBrew Implementation Prompt v2.3 — Phase F (Documentation and release hardening)

> **Read `PROMPT.md` first** — it is sent in the same session and
> contains Part A (the design you must implement) and Part C (the
> checkpoint templates you must fill). This file adds only what is
> specific to this phase. `CHANGES.md` is reference material for the
> project owner and is never sent to implementation sessions. `RATIONALE.md` is also sent with this session: the ADRs document the reasoning it contains.

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

## Phase F — Documentation and release hardening

**Scope:** every A18 document (including the additional ADRs and
the package-author guide's `persist`/`writesToInstallDir` sections);
PowerShell completion; `ci.yml` and `release.yml` (per-RID locked
restore; zip artifacts with `SHA256SUMS.txt` covering every shipped
file; a CI test that fails when the zip contents differ from
`scripts/publish-files.txt`); SBOM; checksum files; the signing
workflow wired to a protected environment, using the
eligibility-checked signing route (A18); non-functional measurements
(A17, including the first-run
metric, the search median-of-10 measurement, and the extraction
verification evidence); the release-readiness report (Part C2).

**Acceptance**
- The CI matrix is green, or the readiness report lists each blocker.
- Release dry-run produces both architecture zips (the
  `scripts/publish-files.txt` set — at minimum `wbrew.exe`,
  `e_sqlite3.dll` and `wbrew-shim.exe`), `SHA256SUMS.txt` and the
  SBOM, plus either valid signatures or artifacts clearly labeled
  unsigned.
- `cli-reference.md` documents the full exit-code table (including 13
  and the 130-vs-`0xC000013A` note).
- Every A19 criterion appears in the readiness report with evidence.
