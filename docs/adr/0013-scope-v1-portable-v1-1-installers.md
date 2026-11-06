# ADR-0013: Scope — v1.0 portable per-user, v1.1 installers (D1)

**Status:** accepted (Phase F, 2026-11-06; records decisions applied since Phase 0)

## Decision
- **v1.0:** per-user scope, ZIP portable packages only (Phases A–D).
- **v1.1:** MSI (per-user and per-machine) and explicitly approved EXE
  installers (Phase E, ADR-0012).
- The phases ran 0, A–D, E, F: Phase F prepares the first public release and
  its readiness report covers the v1.0 criteria (A19 1–12) **and** the v1.1
  additions. A portable-only v1.0 can still be cut from the tree after Phase D
  (commit `b327dd9`).
- Scope never changes silently: a per-machine operation needs `--scope
  machine`, `--allow-installer` and UAC elevation through the helper; an MSI
  installed in a different context than requested is recorded as it actually
  is, with a warning.

## Consequences
- The v1.0 validator path still exists (`ManifestValidator.Validate(…,
  allowV11: false)`) and is unit-tested; the shipped parser validates as v1.1.
- Known limitation: per-machine installs are recorded in the installing
  user's database only.
