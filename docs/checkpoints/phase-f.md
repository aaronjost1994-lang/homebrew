# Checkpoint: Phase F — 2026-11-06

## Environment
- OS / arch: Linux (Debian 13) x64 — dev host, not Windows 11 (A2).
- .NET SDK (exact): 10.0.403. No PowerShell, no Windows, no GitHub Actions
  runner in this environment.
- Could execute builds/tests: yes (release + test-hooks builds, all test
  projects, Docker, Linux-hosted publish of wbrew.exe and the helper for both
  RIDs). Workflows, PowerShell scripts and Windows-only tests: NOT RUN.

## Sub-session and phase status
- Sub-session: **full** (documentation-heavy; production changes were small).
- Phase status: **COMPLETE as an implementation phase** — every Phase F
  deliverable exists — but the release it prepares is **NOT READY**: the
  acceptance item "CI matrix green, or the readiness report lists each
  blocker" is met by the second branch (`docs/release-readiness.md` lists
  every blocker with evidence).

## Decisions applied / assumptions made
- A17, A18, A19, C2; D1 (readiness covers v1.0 and v1.1).
- `scripts/publish-files.txt` gains a machine-readable release-zip set
  (non-comment lines): `wbrew.exe`, `e_sqlite3.dll`, `wbrew-shim.exe`,
  `WinBrew.ElevatedHelper.exe`. The CI test compares the zip against it.
- `SHA256SUMS.txt` lists every loose shipped file per architecture
  (`win-<arch>/<file>`) and both zips, in `sha256sum` format.
- Signing: managed signing service via `azure/trusted-signing-action` behind
  the `release-signing` environment; without its secrets the release is
  labeled UNSIGNED (`SIGNING.txt`, `-UNSIGNED` in zip names and the title)
  and the package job fails if any binary's signature state contradicts the
  label. Action/tool versions (trusted-signing-action v0.5.1, CycloneDX 5.5.0,
  WiX 5.0.2, `windows-11-arm`) are recorded as "verify before use".
- The helper is published self-contained single-file (~101 MB); trimming or
  Native AOT is deferred to a Windows evaluation.

## Files changed
| Path | Change |
|---|---|
| docs/{architecture,cli-reference,manifest-spec,repository-trust-spec,package-author-guide,security-model,threat-model,recovery-troubleshooting,known-limitations,release-checklist,release-readiness}.md | new |
| docs/adr/0013…0021-*.md, docs/adr/README.md | new (D1, D3, D7, D8, D10, D11, x86 fallback, sidecar exception, exit 130; index) |
| .github/workflows/{ci,release}.yml | new |
| scripts/{package-release,wbrew-completion}.ps1, scripts/publish-files.txt | new / changed |
| tests/WinBrew.EndToEndTests/ReleaseArtifactTests.cs, tests/WinBrew.TestFixtures/PlatformFacts.cs | new / changed (ArtifactFact) |
| tests/WinBrew.IntegrationTests/MeasurementTests.cs, tests/WinBrew.UnitTests/TrustChainTests.cs | new / changed (A17 list, example files) |
| examples/bundles/dev.v1{,.lock}.json, examples/README.md | new |
| src/WinBrew.Application/SelfManagement/{SelfInstall,SelfUpdate}UseCase.cs, src/WinBrew.Cli/CommandTree.cs | copy the helper; comment |
| README.md, CONTRIBUTING.md, SECURITY.md, CHANGELOG.md | changed |

## Build and test results (actual output only)
| Command | Result | Evidence |
|---|---|---|
| dotnet build -c Release (release + test-hooks) | PASS | 0 warnings, 0 errors |
| dotnet restore --locked-mode | PASS | no lock file changes (RID publishes restored from backup) |
| dotnet test (test-hooks build) | PASS | Unit 597/597; Integration 220 passed, 22 skipped (Windows-only); E2E 1 passed, 5 skipped (release artifacts) |
| dotnet test (release build) | PASS | Unit 597/597; Integration 74 passed, 93 skipped; E2E 1 passed, 5 skipped |
| docker build --target test | PASS | release pass 597 + 74/93 skipped; test-hooks pass 597 + 220/22 skipped |
| dotnet publish src/WinBrew.Cli + src/WinBrew.ElevatedHelper -r win-x64/win-arm64 (Linux host) | PASS | CLI output = manifest set (wbrew.exe, e_sqlite3.dll, *.pdb); sizes in the readiness report |
| ci.yml / release.yml | NOT RUN | YAML parses; run on GitHub after push |
| package-release.ps1, wbrew-completion.ps1, build-installer-fixtures.ps1, new-fixture-repo.ps1 | NOT RUN | no PowerShell here; the `[suggest]` directive the completion script uses was verified against the CLI |

## Phase F acceptance (phase-f.md)
| Item | Evidence | Status |
|---|---|---|
| CI matrix green, or the readiness report lists each blocker | `docs/release-readiness.md` (blockers per row + external prerequisites) | PASS (blockers listed) |
| Release dry-run: both zips (publish-files set), SHA256SUMS.txt, SBOM, signed or labeled unsigned | `release.yml` (`workflow_dispatch` = dry run), `package-release.ps1`, `ReleaseZipTests` | NOT RUN (needs Windows: AOT shim) |
| `cli-reference.md` documents the full exit-code table incl. 13 and the 130/`0xC000013A` note | `docs/cli-reference.md` | PASS |
| Every A19 criterion in the readiness report with evidence | `docs/release-readiness.md` (1–12 + v1.1) | PASS |

## Known gaps and deferred items
1. First CI run (push to GitHub) and the release dry run (`workflow_dispatch`).
2. The A16 HTTPS end-to-end lifecycle suite is not built (release blocker).
3. Signing route, production repository, root keys, self-update feed (owner).
4. Helper size; previous-version pruning after self update; disk-full seam.

## Concerns about locked decisions
- None.

## Next session must start with
- Push, then read the `ci` workflow results; fix Windows failures; build the
  HTTPS E2E suite; run `release.yml` as a dry run; update
  `release-readiness.md` with run links and A17 numbers.
