# WinBrew Implementation Prompt v2.3 — Phase C (Dependency resolution and upgrade recovery)

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

## Phase C — Dependency resolution and upgrade recovery

**Scope:** PubGrub resolver in Core (pure; consumes the catalog snapshot
per D2); "active constraints" definition and the two-concern decision
heuristic (which package: ordinal `<repo>/<id>`; which version: rules
1–3); explanations (text and JSON, naming repository-qualified IDs and
original range strings); the dependency ID qualification rules (A9:
unqualified = declaring repository; cross-repository fully qualified
or exit 3); architecture filtering per the A10 selection order and PE
machine-type table; **architecture re-selection on upgrade** (the
plan shows `x64 → arm64` when it changes; state and lockfiles record
the new architecture); pins; cycle detection; multi-package
plans; the **manifest-vs-index exact-match check** (exit 5, no
changes — exercised here against fixture index data, re-verified in
Phase D with signed metadata); explicit and dependency tracking;
dependency edges; `upgrade`, `outdated`, `pin`, `unpin`, `autoremove`,
`cleanup`; upgrade compensation and version retention.

**Acceptance**
- The resolver test suite includes classic PubGrub conflict cases,
  diamond dependencies, pinned conflicts, cycles and an
  architecture-unavailable case. It is deterministic: the same input
  gives the same plan across 100 randomized metadata orderings, and
  the same dependency set gives the same plan whether or not another
  repository containing a same-named package is configured.
- The resolver never performs I/O during resolution (verified by
  construction/test: it receives only the in-memory snapshot).
- A manifest whose `dependencies` or `architectures` disagree with the
  catalog entry fails with exit 5 and changes nothing.
- A conflict causes zero filesystem or state changes (verified by
  snapshot comparison).
- A failed upgrade at every checkpoint leaves the previous version
  usable.
- Cleanup never removes active, pinned, referenced or retained
  artifacts.
