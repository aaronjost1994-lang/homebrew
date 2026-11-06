# Checkpoint: Phase E — 2026-11-06

## Environment
- OS / arch: Linux (Debian 13) x64 — dev host, not Windows 11 (A2).
- .NET SDK (exact): 10.0.403.
- Could execute builds/tests: yes for builds, unit tests and the CLI wiring;
  **no** for msiexec, EXE installers, UAC, NTFS ACLs, junctions and hard
  links (Windows only — NOT RUN, see below).

## Sub-session and phase status
- Sub-sessions: **src** (`phase-e-src.md`, INCOMPLETE) then **tests**, both in this session.
- Phase status: **COMPLETE on the Linux dev host for everything that can run here;
  the Windows-runner acceptance items are committed as tests that skip with
  their reason (NOT RUN).** They must pass on disposable Windows runners
  (Phase F CI) before v1.1 is released.

## Decisions applied / assumptions made
- A12 (MSI, EXE, elevated helper), A8 (`--allow-installer`, `--scope machine`),
  A14 (`self uninstall` refusal), D9 (13 `InstallerBusy`). ADR-0012 records
  the design; `phase-e-src.md` lists the assumptions.
- Tests sub-session fix: per-machine installs stage a `.msi`/`.exe`-named copy
  for the helper (the cache is content-addressed without extensions); the
  helper copies it again into its protected `<op>` directory.

## Files changed (tests sub-session)
| Path | Change | Notes |
|---|---|---|
| tests/WinBrew.UnitTests/InstallerTests.cs | new | 33 tests (49 cases with theory data): exit codes, arguments, 1618 retries, helper request, DACL rules, manifest rules, InstallerService flows (fakes), no-shell scan |
| tests/WinBrew.UnitTests/ManifestValidatorTests.cs | changed | typed installer |
| tests/WinBrew.IntegrationTests/InstallerCliTests.cs | new | consent, scope, "installed alone", list, self uninstall refusal |
| tests/WinBrew.IntegrationTests/InstallerWindowsTests.cs | new | 8 Windows-runner tests (directory attacks, ACL snapshots, real MSI/EXE) |
| tests/WinBrew.IntegrationTests/MigrationTests.cs, TestFixtures/FixtureRepository.cs | changed | v3 table; installer fixture packages |
| src/WinBrew.Application/Installers/InstallerService.cs | changed | staged copy for the helper |
| docs/adr/0012-…md, this file, README, CHANGELOG | new/changed | |

## Build and test results (actual output only)
| Command | Result | Evidence |
|---|---|---|
| dotnet build -c Release (release + test-hooks) | PASS | 0 warnings, 0 errors |
| dotnet restore --locked-mode | PASS | new `WinBrew.ElevatedHelper/packages.lock.json`; no other lock changes |
| dotnet test (test-hooks build) | PASS | Unit **596/596**; Integration **219 passed, 22 skipped** (Windows-only) |
| dotnet test (release build) | PASS | Unit 596/596; Integration 74 passed, 92 skipped |
| docker build --target test | PASS | release pass 596 + 74/92 skipped; test-hooks pass 596 + 219/22 skipped |
| scripts/build-installer-fixtures.ps1 (WiX MSI + fixture EXE) | NOT RUN | needs Windows + WiX (Phase F CI) |

## Phase E acceptance (phase-e.md)
| Item | Evidence | Status |
|---|---|---|
| Real install/uninstall of the fixture MSI (per-user, per-machine) and fixture EXE | InstallerWindowsTests.FixtureMsi_PerUser…, FixtureMsi_PerMachine…, FixtureExe_PerUser… | NOT RUN (Windows runner + CI fixtures) |
| Every documented msiexec exit code maps correctly, incl. 1618 → 13 | MsiRulesTests (8 codes), Busy1618 retries, InstallerServiceTests.FailedInstalls (1618/1603/1602), RebootRequired | PASS (unit); real success/failure runs NOT RUN |
| Pre-created `%ProgramData%\WinBrew` (plain dir, junction, hard link) refused; no ACL changed outside helper-created dirs | DirectoryTrustRulesTests (owner, reparse); InstallerWindowsTests PreCreated* with SDDL snapshots | PASS (rules) / NOT RUN (NTFS) |
| Artifact swap defeated by the held handle + hash-from-handle | InstallerServiceTests (held during run; hash mismatch → 5, nothing runs); helper code path | PASS (unit) / NOT RUN (Windows) |
| Administrators-owned dir granting Users:Modify refused | DirectoryTrustRulesTests.AdministratorsOwned_GrantingUsersModify…; InstallerWindowsTests same | PASS (rules) / NOT RUN (NTFS) |
| Directory created between check and create → ERROR_ALREADY_EXISTS re-check | ProtectedDirectories.EnsureTrustedOrCreate; InstallerWindowsTests.CreatedByTheHelper… | NOT RUN (Windows) |
| No code path builds a shell command string | NoShellCommandStringTests (source scan; only the runas launch allowed) | PASS |
| `self uninstall` refuses while installer-managed packages are installed | InstallerCliTests.InstallerManagedPackages_… | PASS |
| `--allow-installer` / `--scope machine`; reconciliation in list/doctor | InstallerCliTests, InstallerServiceTests.Reconcile_… | PASS |

## Fault checkpoints covered
No new checkpoints: installer operations are not journaled (A12 promises no
rollback for MSI/EXE). The registry stays at 24 names.

## Known gaps and deferred items
1. Every Windows-runner item above (real msiexec/EXE runs, NTFS attacks,
   ACL snapshots, UAC) — Phase F CI on disposable runners.
2. Release packaging must add `WinBrew.ElevatedHelper.exe` to the zip
   (`scripts/publish-files.txt`, `self install` copy list) — Phase F.
3. Installer packages cannot have dependencies, are installed alone, and are
   not upgraded by `upgrade` (v1.1 limitation, documented).
4. Per-machine installs are visible only to the installing user's WinBrew.

## Concerns about locked decisions
- None. Remaining risk (documented): malware already running as the same user
  can influence what that user approves at a UAC prompt.

## Next session must start with
- PROMPT.md + `phases/phase-f.md` + this checkpoint (Phase F must run the
  NOT RUN items on Windows runners).
