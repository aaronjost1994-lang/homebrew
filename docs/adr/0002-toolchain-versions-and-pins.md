# ADR-0002: Toolchain versions and package pins (A4)

**Status:** accepted (Phase A src sub-session, 2026-11-05)

## Decision

| Component | Pinned | Evidence / rationale |
|---|---|---|
| .NET SDK | **10.0.403** (`global.json`, `rollForward=latestPatch`) | A4 says "latest patched 10.0.1xx"; PROBES.md §4 (follow-up, 2026-11-05) mandates a newer SDK: the SDK-injected `Microsoft.DotNet.ILCompiler`/runtime packs cannot be pinned under CPM (NU1009), and 10.0.1xx bundles vulnerable 10.0.9 (CVE-2026-111497, NU1903). 10.0.403 bundles the 10.0.14 servicing level. |
| System.CommandLine | **2.0.14** | Latest stable of the 2.0 line on nuget.org (2026-11-05). PLAN.md §2.9 recorded 2.0.11 (fetched 2026-11-04); 2.0.12–2.0.14 shipped since. A4's anchor is "stable 2.0 GA surface" — 3.0 previews stay out of scope. API notes: `Option<T>(name, alias…)` — descriptions are the `Description` property; `Symbol.Name` is read-only (root name = entry assembly name = `wbrew`). |
| Microsoft.Data.Sqlite | **10.0.14** | Pulls SQLitePCLRaw.bundle_e_sqlite3 **2.1.13** — patched for GHSA-2m69-gcr7-jv3q (affected ≤ 2.1.11; the NU1903 in PROBES.md §4). Verified via OSV API; restore shows no NU1903. |
| Microsoft.Extensions.Hosting | 10.0.14 | Matches the 10.0.14 servicing level. |
| NuGet.Versioning | 7.9.0 | Latest stable (D3 versions/ranges). |
| Spectre.Console | 0.57.2 | Latest stable; interactive output only (A4). |
| System.Formats.Asn1 | 10.0.14 (pin only) | Needed from Phase D (D6 SPKI parsing); pinned now, referenced when used. |
| xUnit | 2.9.3 / runner 4.0.1 / Test SDK 18.11.0 | Latest stable. |

Central package management with exact pins; `RestorePackagesWithLockFile=true`;
per-RID lock files restored for `win-x64` and `win-arm64` (A4) and committed.

## Consequences

- Windows CI must use an SDK ≥ 10.0.403 (PROBES.md §4 note).
- When `RuntimeIdentifiers` changes, lock files are regenerated (A4).

### Locked-mode restore contract (Phase A, verified 2026-11-05)

- The stable CI restore command is **`dotnet restore --locked-mode`** at
  solution level. It validates the whole graph INCLUDING both published-RID
  sections (`win-x64`, `win-arm64`) carried in `WinBrew.Cli`/`WinBrew.Shim`
  locks (those projects declare `RuntimeIdentifiers`, so one plain restore
  captures both RID graphs — verified in the committed lock files).
- **Per-RID `-r` locked restores of the full graph are NOT expressible**
  (NuGet NU1004): RID-less projects (all `net10.0` libraries and test
  projects) get RID-tagged project sets under `-r` that can never match
  their deliberately RID-less locks, and the host RID leaks into the set
  (observed: `linux-x64;win-x64` vs lock `linux-x64;win-arm64;win-x64`).
- **Lock files are host-shaped**: a lock generated on Linux carries a
  `linux-x64` section; on a Windows host it would not. CI on windows-latest
  must therefore either run on hosts whose RID set matches the committed
  locks, or regenerate the locks and fail on drift (git diff check) — the
  exact recipe is a Phase F ci.yml decision, recorded here so it is not
  rediscovered.
