# ADR-0019: Architecture selection and the x86 fallback policy (A10)

**Status:** accepted (Phase F, 2026-11-06; implemented in Phase B, `HostArchitectureRules`)

## Decision
- The host is the NATIVE machine (`IsWow64Process2`, falling back to
  `OSArchitecture`), never the process architecture.
- Artifact order, first available wins:

  | Host | Accepted artifact architectures, in order |
  |---|---|
  | x64 | `x64` → `x86` |
  | arm64 | `arm64` → `x64` → `x86` |

- An `x86` artifact is used only when no native artifact exists; the
  fallback appears in the plan and as a `--json` warning.
- Selection re-runs on every install and upgrade; a change (`x64 → arm64`)
  is shown in the plan and recorded in state and lockfiles.
- PE machine types accepted per host: x64 — `0x8664`, `0x014C` (includes
  IL-only/AnyCPU .NET Framework images); arm64 — `0xAA64` (incl. ARM64X),
  `0x8664` (incl. ARM64EC), `0x014C`. ARM32 (`0x01C4`) is refused.

## Evidence
`PhaseBRulesTests`, `LifecycleCliTests.X86OnlyArtifact_…`, `DependencyCliTests.Upgrade_ReSelectsTheArchitecture_…`.
