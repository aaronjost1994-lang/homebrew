# Checkpoint: Phase E (src sub-session) — 2026-11-06

## Environment
- OS / arch: Linux (Debian 13) x64 — dev host, not Windows 11 (A2).
- .NET SDK (exact): 10.0.403.
- Could execute builds/tests: builds yes; MSI/EXE, UAC and NTFS ACL behavior
  only on Windows runners (NOT RUN here).

## Sub-session and phase status
- Sub-session: **src**. Phase status: **INCOMPLETE** (tests sub-session follows in this session).

## Decisions applied / assumptions made
- A12 MSI/EXE/elevated helper, A8 consents (`--allow-installer`,
  `--scope machine`), A14 `self uninstall` refusal, D9 13 `InstallerBusy`.
- Typed `installer` declaration (manifest): `msi { productCode, upgradeCode?,
  properties, userProperties }` / `exe { silentInstallArgs, silentUninstall
  { path, args }, detection { uninstallKey?, file { path, version? }? } }`.
  Paths are patterns rooted at `{ProgramFiles}`, `{ProgramFilesX86}`,
  `{ProgramData}`, `{LocalAppData}`, `{AppData}`.
- A 'user' MSI must declare `userProperties` (the per-user property set it was
  authored for); `ALLUSERS`/`MSIINSTALLPERUSER`/`REBOOT*` cannot appear in
  `properties`. Property values use a conservative alphabet (no spaces,
  quotes or control characters).
- v1.1 limitation: installer packages have no dependencies and are installed
  alone (a plan mixing them with other changes is refused, exit 6);
  `upgrade` does not touch them.
- `--allow-installer` is required for MSI/EXE install AND uninstall (exit 10
  otherwise); `--scope machine` selects the elevated helper.
- Installer packages live in a separate table (`installer_packages`, schema
  migration v3) with the declaration recorded for uninstall.
- The only `UseShellExecute = true` is the helper launch with the `runas`
  verb (one argument via ArgumentList).

## Files changed
| Path | Change |
|---|---|
| src/WinBrew.Core/Installers/{InstallerModels,MsiRules,DirectoryTrustRules,ElevatedRequest}.cs | new |
| src/WinBrew.Core/Manifest/{Manifest,ManifestValidator,ManifestParser}.cs | typed installer, v1.1 rules |
| src/WinBrew.Application/Ports/IInstallers.cs, Installers/InstallerService.cs | new |
| src/WinBrew.Application/{Packages/{PlanExecutor,InstallUseCase,UninstallUseCase,QueryUseCases},Doctor/LifecycleDoctorChecks,SelfManagement/SelfUninstallUseCase}.cs | wiring |
| src/WinBrew.Infrastructure/Sqlite/{Migrations,SqliteStateStore}.cs | migration v3, installer store |
| src/WinBrew.Windows/Installers/{InstallerAdapters,ProtectedDirectories}.cs | new |
| src/WinBrew.ElevatedHelper/* | new project (in WinBrew.sln, Dockerfile) |
| src/WinBrew.Cli/{PackageCommands,ServiceConfigurator,Output/EnvelopeModels}.cs | flags, DI |
| tests/fixtures/installers/*, scripts/build-installer-fixtures.ps1 | CI-built fixtures |

## Build and test results (actual output only)
| Command | Result |
|---|---|
| dotnet build src/* -c Release | PASS (0 errors) |
| dotnet build WinBrew.sln | FAIL (expected): ManifestValidatorTests uses the old untyped installer |

## Next sub-session must start with
- Unit tests (exit codes, arguments, request parsing, DACL rules, detection,
  service flows with fakes, the no-shell-string rule), Windows-only
  integration tests (skipped here), migration v3, final checkpoint.
