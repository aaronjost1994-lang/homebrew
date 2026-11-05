# WinBrew (`wbrew`)

An independent, Windows-native CLI package manager on .NET 10.
Per-user portable installs (ZIP), shims on PATH, signed repository
metadata (simplified TUF), PubGrub dependency resolution, and no
administrator rights required.

**Status:** Phase A (implementation) — *in progress*. The design and
phased implementation prompts live in a separate planning repository.

Conceptual references (no affiliation, no format compatibility):
Homebrew (UX), Scoop (portable installs and shims), winget (manifest
prior art), The Update Framework (trust model), PubGrub (resolver),
Cargo's sparse index (catalog model).

## Development

- .NET 10 SDK (pinned in `global.json`)
- `dotnet build` — warnings are errors
- `dotnet test` — xUnit
- Package versions are centrally pinned (`Directory.Packages.props`)
  and locked per RID (`packages.lock.json`, committed)

See `CONTRIBUTING.md` and `SECURITY.md`.

## License

MIT — see `LICENSE`.
