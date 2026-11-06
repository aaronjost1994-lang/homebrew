# ADR-0016: Uninstall and orphans (D8)

**Status:** accepted (Phase F, 2026-11-06; implemented in Phases B–C)

## Decision
- `uninstall` refuses (exit 6) while another installed package depends on the
  target and names the dependents (dependency edges are stored at plan commit,
  ADR-0010).
- Packages installed only as dependencies (`explicit = 0`) stay installed when
  their dependents go. `wbrew autoremove [--dry-run]` removes non-explicit,
  unpinned packages that no installed package references, repeatedly
  (dependents first), each as its own journaled uninstall.
- Installing a dependency by name promotes it to explicit; explicitness is
  never demoted.
- Installer-managed packages (v1.1) have no dependency edges and are never
  autoremoved.

## Evidence
`DependencyCliTests` (dependents refused, autoremove), `CrashRecoveryTests` (Uninstall).
