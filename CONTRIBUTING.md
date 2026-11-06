# Contributing

## Build and test

- Use the pinned SDK (`global.json`, .NET 10.0.403). Warnings are errors,
  analyzers run at `latest-recommended`, code style is enforced in the build.
- `dotnet restore WinBrew.sln --locked-mode` — package versions are pinned in
  `Directory.Packages.props`; commit `packages.lock.json` changes deliberately
  (RID-specific restores of `src/WinBrew.Cli`, `src/WinBrew.Shim`,
  `src/WinBrew.ElevatedHelper` must not leave changes; CI checks this).
- `dotnet build -c Release` and `dotnet test` (release build), then
  `dotnet build -c Release -p:WinBrewTestHooks=true` and `dotnet test` again:
  the crash-at-every-checkpoint matrix and the CLI integration tests need the
  test-hooks build.
- On Linux, `docker build --target test .` runs both passes in the pinned SDK
  image. Windows-only behavior is verified by `.github/workflows/ci.yml`.

## Rules

- Never add test-only behavior outside the `-p:WinBrewTestHooks=true` gate
  (fault injection, the localhost HTTP exception, `testing.*` keys,
  `WINBREW_TEST_NOW`, the test release feed).
- Never fake a result: no placeholders, `NotImplementedException` or silent
  success on a critical path; a test that cannot run here skips with its
  reason (`WindowsFact`, `TestHooksFact`, `ArtifactFact`).
- No shell command strings: processes start through `ProcessStartInfo.ArgumentList`
  (`NoShellCommandStringTests` enforces it).
- Every durable state transition gets a registered fault checkpoint
  (`FaultCheckpoints`) and a row in `CrashRecoveryTests.Coverage`.
- Exit codes map in one place (`src/WinBrew.Cli/ExitCodes.cs`); JSON output
  follows `schemas/cli-output.v1.schema.json`.
- Decisions that change behavior get an ADR in `docs/adr/` and a CHANGELOG
  entry.

## Pull requests

Keep them focused, include tests, and describe what you could not test
(for example Windows-only paths on a non-Windows machine). Security issues:
see `SECURITY.md` — never in a public issue.
