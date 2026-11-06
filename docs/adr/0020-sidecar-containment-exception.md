# ADR-0020: Sidecar containment and its single exception (D4)

**Status:** accepted (Phase F, 2026-11-06; implemented in Phase B, see ADR-0007)

## Decision
- A shim sidecar's `target` must be an absolute path inside
  `<root>\packages\`, where `<root>` is ALWAYS the parent of the shim's own
  directory (`bin\..`) — never `WINBREW_ROOT` or any other environment
  variable.
- **The only exception** is `wbrew.shim.json`, the WinBrew self-shim, whose
  target must be inside `<root>\app\` (it starts `app\<ver>\wbrew.exe`).
- Containment normalizes the full path with a trailing separator and refuses
  a reparse point anywhere along the path. Violations write one stderr line and
  exit **9009**.
- This is defense in depth only: a process running as the same user can
  already write to `packages\` and `bin\`.

## Evidence
`ShimRulesTests` (containment cases), Windows-only shim process tests.
