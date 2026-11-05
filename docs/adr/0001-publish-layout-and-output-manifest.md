# ADR-0001: Publish layout and the publish-output manifest (A4)

**Status:** accepted (Phase A src sub-session, 2026-11-05)
**Context:** A4 requires self-contained, architecture-specific, single-file
**managed** bundles for `wbrew`, with no native self-extraction:
`PublishSingleFile=true`, `SelfContained=true`,
`IncludeNativeLibrariesForSelfExtract=false`, `PublishTrimmed=false`,
`PublishAot=false`. `e_sqlite3.dll` ships next to `wbrew.exe`. `WinBrew.Shim`
is the only Native AOT component (D4).

## Decision

- `WinBrew.Cli` carries exactly those properties plus
  `RuntimeIdentifiers=win-x64;win-arm64`; app.manifest embeds
  `longPathAware=true` (A12) and `requestedExecutionLevel=asInvoker`.
- The exact publish output per RID is recorded in `scripts/publish-files.txt`:
  `wbrew.exe`, `e_sqlite3.dll`, plus PDBs (never shipped in zips). The release
  zip contains that set minus PDBs plus `wbrew-shim.exe` (A14/A18). A CI test
  must fail if the output ever differs.
- **Observed on this build host** (Linux, SDK 10.0.403): `dotnet publish -c
  Release -r win-x64` produced exactly `wbrew.exe` (single-file managed
  bundle, 104 MB), `e_sqlite3.dll`, and the PDBs. No extraction directory is
  involved in this layout: the bundle contains no native libraries, so there
  is nothing to extract.

## Consequences and honest gaps

- The A16/A17 assertion "published binaries never create an extraction
  directory under %TEMP% on a fresh profile" is **NOT RUN here** — it needs a
  real Windows execution of the published binary. Recorded as a Windows-CI
  deliverable; the layout above makes extraction structurally impossible
  (`IncludeNativeLibrariesForSelfExtract=false` — no native libs inside the
  bundle), but the test still must run on Windows.
- `wbrew-shim.exe` is not in this publish output: **cross-OS Native AOT is
  refused** ("Cross-OS native compilation is not supported", PROBES.md §4).
  The shim publishes from Windows CI/runner jobs.
- Single-file bundle size (~104 MB self-contained) is reported per A17; no
  hard limit applies to `wbrew.exe` (only the shim has a < 3 MB target).

## Extraction verification (Phase B, 2026-11-06)

`ExtractionVerificationTests.PublishedBinary_CreatesNoExtractionDirectory`
runs the published `wbrew.exe` (`--version`, `config list`, `doctor --json`)
with a fresh, empty `%TEMP%` and asserts it stays empty. **Result: NOT RUN
yet** — it needs Windows and the published binary (`WINBREW_PUBLISHED_EXE`);
Windows CI (Phase F) records the observed result here.
