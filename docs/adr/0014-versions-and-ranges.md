# ADR-0014: Versions and ranges (D3)

**Status:** accepted (Phase F, 2026-11-06; implemented in Phase A, `VersionOps`)

## Decision
- `version` must parse as a NuGet.Versioning `NuGetVersion` (pinned 7.9.0)
  and must already be in `ToNormalizedString()` form; manifests, index
  version keys, lockfiles and release indexes are rejected otherwise (strict,
  not silently normalized, so digests and identities stay stable).
- `vendorVersion` is optional, free-form and display-only (≤ 64 chars).
- Ranges use **NuGet interval notation only** (`[1.0,2.0)`, `[1.2.3]`,
  `(,3.0)`). A bare `1.2.3` is **rejected**: NuGet reads it as `>= 1.2.3`,
  which surprises authors. Ranges are stored verbatim (index, catalog) and
  shown verbatim in conflict explanations.
- Prereleases are candidates only when the range or the requested version
  names a prerelease (D2 rule 6).

## Evidence
`VersionOpsTests`, `ManifestValidatorTests`, `ResolverTests`.
