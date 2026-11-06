# WinBrew architecture

WinBrew (`wbrew`) is a per-user Windows package manager written in .NET 10.
This document describes how the pieces fit together; the reasons live in the
ADRs (`docs/adr/README.md`).

## Projects (A5)

```
src/
  WinBrew.Core            pure domain: identifiers, versions/ranges (D3), manifest model + validator (A9),
                          PubGrub resolver (D2), trust chain (D6), bundles/lockfiles (A15), release index (A14),
                          installer rules (A12), fault-checkpoint registry (A11). No I/O.
  WinBrew.Application     use cases (one per command) and ports (interfaces) — install/upgrade planning,
                          plan execution as a journaled operation, recovery, repo/update/search,
                          bundles, self install/update/uninstall, installer service, doctor.
  WinBrew.Infrastructure  SQLite (schema v1–v3, migrations), HTTP client (A13), ZIP extraction defenses,
                          content-addressed cache, config store, mutation lock, fault injector.
  WinBrew.Windows         Windows adapters: host architecture, PATH (HKCU), shim store, Restart Manager,
                          Credential Manager, msiexec/EXE runner, msi.dll, detection probe,
                          protected directories (helper), elevated-helper launcher.
  WinBrew.Cli             System.CommandLine tree, output (JSON envelope), exit-code mapping, composition root.
  WinBrew.Shim            Native AOT shim template (wbrew-shim.exe, D4).
  WinBrew.ElevatedHelper  v1.1 elevated helper (A12).
```

Dependencies point inward: Cli → Application → Core; Infrastructure and
Windows implement Application ports and are referenced only by the Cli for
registration. `--version` takes a fast path that never builds the DI
container.

## Storage layout (A6)

`%LOCALAPPDATA%\WinBrew\` (or `WINBREW_ROOT`):

| Path | Contents |
|---|---|
| `app\<ver>\` | `wbrew.exe`, `e_sqlite3.dll`, `wbrew-shim.exe`, `WinBrew.ElevatedHelper.exe` (one dir per WinBrew version) |
| `bin\` | one shim copy `<name>.exe` + `<name>.shim.json` per command; `wbrew.exe` is the self-shim |
| `packages\<id>\<version>\<arch>\` | extracted portable packages (active + `retainVersions` inactive) |
| `cache\artifacts\` | content-addressed artifacts (SHA-256) |
| `cache\metadata\<repo>\<id>\<version>.json` | verified manifests |
| `staging\<op>\`, `staging\elevated\` | per-operation work areas (same volume as `packages\`) |
| `state\winbrew.db`, `state\winbrew.lock`, `state\backups\` | SQLite state, mutation lock, pre-migration backups |
| `logs\` | diagnostics and installer logs |
| `config.json` | D10 configuration |

## Main flows

**Trust and catalog (D6).** `repo add` → `root.json` (self-signed, fingerprint
approval) → `update` walks `root/<N+1>.json` → `timestamp.json` →
`index.json` (length + SHA-256 bound by the timestamp) → ONE SQLite
transaction with the catalog, the search index and every role high-water
mark. Planning and `search` read only this catalog.

**Install / upgrade (D2, A10, A11).** The use case takes the mutation lock,
runs startup recovery, refreshes expired metadata (A8), builds an in-memory
snapshot of every reachable catalog package and runs the pure PubGrub
resolver. The plan (dependencies first) is shown for approval. The executor
fetches and checks every manifest against its signed index entry BEFORE the
journaled operation starts, then per package: artifact (cache or download,
SHA-256) → extract (A13 defenses) → validate (PE machine, shim collisions) →
place → shims; one `StateCommitted` transaction records packages, shims and
dependency edges; roll-forward cleanup follows. A crash before the commit is
compensated (previous versions stay usable); after it, recovery rolls forward.

**Shims (D4).** `bin\<name>.exe` is a copy of the active template; the sidecar
names the target. The shim parses its own command line with the MSVC
`argv[0]` rules, forwards the tail verbatim, enforces containment, runs the
child in a job object and forwards Ctrl+C.

**Installers (v1.1, A12).** MSI/EXE packages are installed alone with
`--allow-installer`. Per-user operations run msiexec/the EXE directly from a
held, hash-verified copy; per-machine operations go through
`WinBrew.ElevatedHelper.exe` (runas) with a typed request. Results are
confirmed (MSI context / EXE detection) and recorded in `installer_packages`;
`list`/`doctor` reconcile without correcting.

**Self management (A14).** `self install` copies the release files into
`app\<ver>\`; `self update` verifies the signed feed (same trust model),
places the new version and switches every shim in one journaled operation;
`self uninstall` removes PATH entry, shims and (optionally) data.

## Cross-cutting rules

- One exception type (`WinBrewException`) carries a D9 code; the CLI maps it
  centrally (`ExitCodes.cs`) and renders the JSON envelope.
- Every durable transition is a named fault checkpoint (24 in the registry);
  the test-hooks build can crash the real process at any of them (A11/A16).
- Test-only behavior is compiled in only with `-p:WinBrewTestHooks=true`.
