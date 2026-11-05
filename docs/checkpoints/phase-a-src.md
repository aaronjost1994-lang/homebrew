# Checkpoint: Phase A — src sub-session — 2026-11-05

## Environment
- OS / arch: Linux (Debian 13) x64 — **NOT the expected Windows 11 host (A2
  disclosure)**. Dev-host workarounds: `EnableWindowsTargeting=true` builds
  the `net10.0-windows` projects; `libicu76` installed for the .NET runtime.
- .NET SDK (exact): **10.0.403** (`global.json` pins; latest 10.0.x, bundling
  ILCompiler/runtime ≥ 10.0.14 per PROBES.md §4).
- Could execute builds/tests: builds **yes** (all ten projects, warnings as
  errors, 0 warnings). Runtime tests: `net10.0` yes; `net10.0-windows`
  binaries **run on Linux** only where they touch no Windows API (observed:
  fast path, config, doctor all work; Windows probes degrade to "unknown"
  by design). Native AOT and Windows-API behavior: **NOT RUN — needs
  Windows** (see Known gaps).

## Sub-session and phase status
- Sub-session: **src** (all production code + schemas first, per phase-a.md §3)
- Phase status: **INCOMPLETE** — remaining work: the **tests sub-session**
  (unit + integration: config validation, manifest validation, exit-code
  mapping incl. 13, JSON envelope schema conformance, lock behavior across
  real processes incl. holder-PID report / torn-read / PID-reuse, migration
  + backup tests, FTS5 probe test) and the Windows-only deliverables below.

## Decisions applied / assumptions made
- ADR-0001..0006 (docs/adr/): publish layout + manifest; toolchain pins
  (SDK 10.0.403, SCL 2.0.14 — newer than the plan's recorded 2.0.11, which
  predates 2.0.12–14; MDS 10.0.14 → SQLitePCLRaw 2.1.13 clears GHSA-2m69-
  gcr7-jv3q); storage schema v1 verbatim from PLAN §5; lock mechanics
  (Process.StartTime as the GetProcessTimes equivalent, 1.5 s start-time
  tolerance); **FTS5 = available → adopted for Phase D as migration v2**;
  exit-130 convention, doctor exit semantics, NotAvailableInThisBuild.
- `net10.0-windows` projects set `TargetPlatformIdentifier=Windows`,
  `TargetPlatformVersion=10.0.22000.0` (Windows 11), `SupportedOSPlatform-
  Version=10.0.19041.0` (min supported) — required explicitly, otherwise the
  SDK infers platform 7.0 (NETSDK1135) and mismatches across projects.
- Doctor exit semantics: checks ran + a Fail exists ⇒ exit 1 with envelope
  `ok: true` (ADR-0006.3).
- `config set` is mutating (D5): takes lock, ensures layout. DB migrations
  are NOT run by `config set` (deferred to commands that use the database);
  doctor reports "no database yet" honestly.

## Files changed
| Area | Change |
|---|---|
| Root | `WinBrew.sln` (10 projects), `global.json`, `Directory.Build.props` (nullable, warnings-as-errors, latest-recommended analyzers, code-style enforced, WinBrewTestHooks gate), `Directory.Packages.props` (CPM exact pins), `.editorconfig`, `.gitignore`, LICENSE (MIT), README, CHANGELOG, CONTRIBUTING, SECURITY |
| src/WinBrew.Core | Identifiers (A9), VersionOps (D3), WinBrewErrorCode/Exception (D9), ConfigDefinitions (D10), operation enums + wire names (A11), FaultCheckpoints registry (empty by design — first names in Phase B), PathList (A12 pure string rules), Manifest model + validator (A9, incl. SPDX structural validation, HTTPS-only URLs with test-hooks loopback exception, shim-name/target rules), WinBrewJsonContext (source-gen) |
| src/WinBrew.Application | Ports: IConfigStore (+inspection), IStateDatabase, IMutationLock, IFaultInjector (+NoOp), IEnvironmentProbe (IsWow64Process2 contract), IStorageLayout, IConsentPrompt + InteractivityOptions (A8), IVersionInfo. Use cases: config get/set/list, DoctorUseCase (layout/arch/config/db/lock/FTS5/testhooks checks) |
| src/WinBrew.Infrastructure | StoragePaths (A6 layout, WINBREW_ROOT, one-volume rule), JsonConfigStore (strict load + doctor inspection + atomic save), SqliteStateDatabase (WAL, FK, user_version migrations, backup API) + Migrations (schema v1 = PLAN §5), ProcessMutationLock (full D5), FaultInjectorFactory (test-hooks seam: WINBREW_FAULT_AT name + #N occurrence), InfrastructureJsonContext |
| src/WinBrew.Windows | WindowsEnvironmentProbe: IsWow64Process2 (native machine), LongPathsEnabled, HKCU Path existence, volume identity via GetVolumeInformationW serial, PID+start-time holder check; all degrading safely off-Windows |
| src/WinBrew.Cli | Program (fast-path --version, lazy host, parse-error → exit 2), ExitCodes (single D9 mapping), CommandTree (config get/set/list, doctor, every other A8 command as NotAvailableInThisBuild), GlobalOptions (7 recursive A8 options), CommandRunner (central mapping + JSON envelope), envelope models + CliJsonContext (source-gen, string enums), ConsoleConsentPrompt (exit 10 in non-interactive), CliVersionInfo, ServiceConfigurator (composition root; only Cli references Infrastructure+Windows) |
| src/WinBrew.Shim | csproj with Native AOT config + long-path manifest; Program.cs fails loudly (9009) with "implementation arrives in Phase B" — no fake success (A2) |
| schemas/ | All 8: manifest.v1, repository-root.v1, repository-timestamp.v1, repository-index.v1, bundle.v1, lockfile.v1, config.v1, cli-output.v1 (draft 2020-12) |
| scripts/ | publish-files.txt (exact observed publish output per RID; zip derivation; AOT shim = Windows CI) |
| docs/ | ADR-0001..0006; this checkpoint |
| tests/ | 4 projects scaffolded (UnitTests net10.0; IntegrationTests/EndToEndTests net10.0-windows; TestFixtures) — bodies are the tests sub-session |

## Build and test results (actual output only)
| Command | Result | Evidence |
|---|---|---|
| `dotnet restore` (+ `-r win-x64`, `-r win-arm64`) | PASS | all 10 projects; per-RID packages.lock.json generated; **no NU1903** (2.1.13 clears GHSA-2m69-gcr7-jv3q) |
| `dotnet build -c Release` | PASS | 0 Warnings / 0 Errors (warnings-as-errors, latest-recommended, code-style enforced) |
| `dotnet publish src/WinBrew.Cli -c Release -r win-x64 --self-contained` | PASS | output = wbrew.exe (single-file managed) + e_sqlite3.dll + PDBs; recorded in scripts/publish-files.txt |
| `dotnet publish src/WinBrew.Shim -r <rid>` (AOT) | **NOT RUN** | cross-OS native compilation refused (PROBES.md §4) — Windows CI |
| FTS5 probe (real SqliteStateDatabase) | PASS | **fts5=True** on MDS 10.0.14 / SQLitePCLRaw 2.1.13 (ADR-0005) |
| Migration probe (empty → v1 + backup) | PASS | user_version -1 → 1; 1 backup file created (ADR-0003 evidence) |
| CLI smoke (Linux run of net10.0-windows binary) | PASS | --version → `wbrew 0.1.0 (linux-x64)` exit 0; config get/set/list (defaults not materialized; file atomic); retries=9 → exit 2; unknown key set → 2; get → 3; testing.* in release → 2 (D10); badcommand → 2 (SCL default 1 overridden); `list` → 1 NotAvailableInThisBuild; doctor text+JSON (8 checks, correct off-Windows degradations; stale lock metadata reported via PID+start-time rule); --json envelopes conform to cli-output.v1 (success + error shapes) |
| `dotnet test` | **NOT RUN** | tests sub-session (next) |

## Fault checkpoints covered
| Checkpoint | Test | Result |
|---|---|---|
| (none) | Registry intentionally empty in Phase A — no durable package/journal transitions exist yet; first names (download.*, artifact.*) arrive with Phase B | n/a |

## Known gaps and deferred items
1. **Windows-required, NOT RUN here** (exact commands): shim AOT `dotnet
   publish src/WinBrew.Shim -c Release -r win-x64` and `-r win-arm64`;
   `wbrew --version` / `config *` / `doctor --json` from the **published
   zip** (wbrew.exe + e_sqlite3.dll + wbrew-shim.exe); the no-%TEMP%-
   extraction assertion (A4/A16/A17); registry-backed doctor paths. →
   Windows CI (Phase F workflows) / a Windows machine.
2. **Tests sub-session** (phase acceptance): config/manifest/exit-code/JSON
   envelope unit tests; real-process lock tests (holder PID report,
   torn-read, PID-reuse); migration + backup tests; FTS5 probe test.
3. `packages.lock.json` files are generated and committed; CI must restore
   `--locked-mode -r` per RID (A4) — workflow files are Phase F.
4. scripts/build.ps1, test.ps1, publish.ps1, new-fixture-repo.ps1,
   wbrew-completion.ps1 — deferred (Phase F owns the script set; the
   publish-output manifest exists already).
5. docs/ beyond ADRs (architecture.md, cli-reference.md, …) — Phase F (A18).
6. `WinBrew.ElevatedHelper` project — v1.1 only (A5); created in Phase E.

## Concerns about locked decisions
- None new. D5's "GetProcessTimes" is implemented via `Process.StartTime`
  (same clock on Windows) — noted in ADR-0004 rather than raw P/Invoke.
- A4's "latest patched 10.0.1xx SDK" is superseded by the PROBES.md §4
  security mandate (10.0.4xx band); recorded in ADR-0002.

## Next session must start with
- PROMPT.md + phases/phase-a.md + this checkpoint.
- Implement tests/WinBrew.UnitTests + WinBrew.IntegrationTests (+ TestFixtures
  fakes) per the Known gaps item 2 list; run test-hooks build
  (`-p:WinBrewTestHooks=true`) where fault injection will be needed (first
  real checkpoints arrive in Phase B).
- On a Windows host/runner: shim AOT publish, published-zip smoke, %TEMP%
  assertion — or record them as Windows-CI deliverables for Phase F.
