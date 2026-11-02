# WinBrew Implementation Prompt v2.3 — Phase 0 (Plan — no code)

> **Read `PROMPT.md` first** — it is sent in the same session and
> contains Part A (the design you must implement) and Part C (the
> checkpoint templates you must fill). This file adds only what is
> specific to this phase. `CHANGES.md` is reference material for the
> project owner and is never sent to implementation sessions. `RATIONALE.md` is also sent with this session: Phase 0 must weigh the default decisions recorded in it.

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

## Phase 0 — Plan (no code)

**Deliverables**
- Assumptions and any open questions that Part A does not settle.
- **Default decisions** — the reviews' recommended answers, adopted as
  defaults rather than settled facts. Record each as adopted. If you
  find evidence that contradicts a default, report it in the plan: a
  contradiction does not need permission to be raised, and surfacing
  one is exactly what this planning phase is for:
  1. Extraction behavior: with `IncludeNativeLibrariesForSelfExtract=false`
     no extraction is expected; verify by test in Phase B (fresh
     profile, assert no extraction directory under `%TEMP%`) and record
     the observed behavior in the ADR.
  2. FTS5: Phase A probes whether the bundled SQLitePCLRaw build
     includes FTS5 and records the result; the D6 search implementation
     choice depends on it.
  3. ARM64: check whether a GitHub-hosted Windows ARM64 runner is
     available for this repository; if not, ARM64 E2E reports BLOCKED
     per A16.
  4. Signing: the default Authenticode route is Microsoft's managed
     code-signing service (Trusted Signing — verify its current name,
     "Artifact Signing" as of late 2026, and its identity-validation
     and regional eligibility rules) **if the project owner
     qualifies**; otherwise an OV or EV certificate from a public CA;
     if neither, releases are labeled UNSIGNED and listed as a release
     blocker (A18). Record the route and its owner in
     `release-checklist.md`.
  5. x86: artifacts may declare `x86` as an explicit last-resort
     fallback per the A10 selection order; x86 PE images inside
     x64/arm64 artifacts are always accepted.
  6. Expired metadata: online mutating commands auto-refresh through
     the verified `update` path; read-only commands serve
     expired-but-once-valid metadata with a warning and never refresh
     (A8).
  7. Cross-repository dependencies: allowed only fully qualified, and
     resolved only if the target repository is configured (A9).
     Forbidding them entirely in v1.0 is an option you may recommend,
     with reasons.
  8. E2E runs over HTTPS with a per-job test CA installed into
     `LocalMachine\Root` on disposable runners (A16). If the project
     owner's security policy forbids installing a test root CA, report
     it in the plan — E2E then needs another trusted-HTTPS approach.
  9. System.CommandLine: probe nuget.org for the exact **stable
     2.0.x** package version and record it in the plan (the A4 anchor
     depends on the GA surface). If no stable 2.0 release exists,
     report the closest available version with a recommendation
     before Phase A starts.
- Final repository tree and project reference graph.
- Target frameworks per project — `net10.0` versus a Windows-specific
  TFM for `WinBrew.Cli`, `WinBrew.Windows`, `WinBrew.Shim` and
  `WinBrew.ElevatedHelper` (registry, ACL and `IsWow64Process2`
  needs; the shim is Native AOT) — recorded with rationale.
- SQLite schema v1 (tables, indexes, constraints, including the
  repository catalog tables for D6 — package summaries, version entries
  with dependencies and architectures, and per-role accepted-version
  high-water marks — all written in one ingest transaction).
- The list of fault-injection checkpoint names for A11.
- Phase-by-phase plan with estimated file counts (assuming the
  `src`/`tests` split for Phases A and B).
- Any objections to the D-decisions, with reasons. Do not change them.

**Acceptance:** the project owner reviews and approves this plan before
Phase A.
