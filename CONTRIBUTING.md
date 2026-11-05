# Contributing

Status: WinBrew is being implemented phase by phase from a private
design specification. Contribution workflow (branching, review gates,
CI requirements) will be finalized along with the full docs in the
release phase.

Until then:

- Build with the pinned SDK (`global.json`); warnings are errors.
- Keep package versions pinned through `Directory.Packages.props` and
  commit `packages.lock.json` changes.
- Run `dotnet test` before proposing changes.
- Do not add test-only behavior outside the `-p:WinBrewTestHooks=true`
  build gate (fault injection, the localhost HTTP exception,
  `testing.*` config keys).
