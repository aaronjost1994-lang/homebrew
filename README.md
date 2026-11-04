# WinBrew — Implementation Prompt File Set

**Version:** 2.3.1 (2026-11-03) · **Status:** planning — no implementation yet

This repository contains the phased implementation prompts for
**WinBrew** (`wbrew`), an independent, Windows-native CLI package
manager on .NET 10. It contains *only* the prompts — the
implementation itself is built in phase sessions driven by these
files.

Conceptual references (no code or format compatibility, no
affiliation): Homebrew (UX and command vocabulary), Scoop (per-user
portable installs and the shim model), winget manifest schema (prior
art), The Update Framework (trust model), PubGrub (dependency
resolution), Cargo's sparse index (catalog model).

## File routing — what each session receives

| File | Contents | Sent to |
|---|---|---|
| `PROMPT.md` | Part A — the design (A1–A19, D1–D11); Part C — report templates | **Every** session |
| `phases/phase-0.md` | Phase 0 — plan, default decisions, probes, schema v1 | Phase 0 session |
| `phases/phase-a.md` … `phases/phase-f.md` | One phase prompt each | Its own session only |
| `RATIONALE.md` | Why each decision was made | Phase 0 and Phase F only |
| `CHANGES.md` | Changelogs, review traceability | Never (owner only) |

## Session rules

- Start each session with `PROMPT.md` + exactly **one** phase file +
  the previous phase's checkpoint report.
- Run the phases in order: **0, A, B, C, D, E, F** — one phase per
  session. D1 explains why E (v1.1) precedes F (release hardening).
- Phases A and B split into `src` / `tests` sub-sessions by default.
- A phase counts as complete only after its test sub-session passes.

## Phase map

| Phase | Deliverable |
|---|---|
| 0 | Plan, default decisions, probes (ARM64 runners, signing route, System.CommandLine 2.0), SQLite schema v1 |
| A | CLI scaffolding, config, JSON schemas, SQLite + migrations, mutation lock, `doctor`, FTS5 probe |
| B | Secure portable lifecycle: HTTP client, cache, archive defenses, Native AOT shim, journal/recovery, `self install` |
| C | PubGrub resolver, architecture selection/re-selection, pins, cycles, upgrade compensation |
| D | Simplified-TUF trust, sparse-index catalog, `repo add/list/remove`, `update`, bundles, `self update`/`self uninstall` |
| E (v1.1) | MSI/EXE installers, elevated helper (refuse-never-adopt), `self uninstall` refusal |
| F | Documentation, CI/release workflows, SBOM, signing, release-readiness report |

## Owner tools

- `mindmap.html` — interactive map of the spec, open in a browser.
  Root + six branches (files, decisions, v2.3.1 patch, phases,
  security pillars, session rules); click any card for its detail
  bullets, legend chips fold branches. Owner-side only — it is not
  part of the file routing above and is never sent to sessions.

## Provenance

v2.0 (2026-11-01) → v2.3 through two review rounds (30 + 18 findings,
all addressed and verified). v2.3.1 is a planning-consistency patch
(phase order vs D1, phase assignments, `phases/` layout, bundle
`--locked` syntax, helper request transport, Phase 0 probes). Full
history and traceability: `CHANGES.md`. Decision rationale:
`RATIONALE.md`.
