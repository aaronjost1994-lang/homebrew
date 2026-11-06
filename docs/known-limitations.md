# Known limitations

## By design (A3, D-decisions)

- No GUI, service or background daemon; no side-by-side active versions.
- No `persist` directories: applications that store state in their install
  directory lose it on upgrade (`writesToInstallDir` warns; see the package
  author guide).
- No install/uninstall scripts or hooks; shims only for `.exe` entry points.
- No Microsoft Store, MSIX/AppX or winget-source integration; no building
  from source.
- No per-machine **portable** packages.
- No dependencies on, or from, MSI/EXE installer packages; installer packages
  are installed on their own and are not touched by `upgrade`.
- No telemetry.
- WinBrew never terminates running processes (files in use → exit 11).
- No Mark-of-the-Web: SmartScreen reputation checks do not apply to
  WinBrew-installed binaries (D7).
- Exit 130 for cancellation instead of `0xC000013A` (ADR-0021).
- Rollback is not promised for MSI/EXE operations.

## Current implementation

- Per-machine installer packages are recorded only in the installing user's
  WinBrew database; other users' WinBrew instances do not see them.
- After `self update`, the previous `app\<ver>\` is kept but not pruned
  automatically.
- `self uninstall` is idempotent rather than journaled; re-run it to finish.
  With `--keep-data`, `wbrew doctor` lists the shims to recreate after a
  reinstall.
- Disk-full handling (exit 12) has no fault-injection seam yet; it is
  verified only by code review.
- MSI property values are limited to a conservative alphabet (no spaces).
- `search` uses an FTS5 trigram index for queries of three or more
  characters and a direct scan for shorter ones (same results).

## Release status (see `release-readiness.md`)

- **No public repository and no self-update feed** exist yet; `self update`
  exits 3 in current builds.
- **Code signing** depends on an external signing route; without it, releases
  are published labeled **UNSIGNED**.
- Windows-only behavior (shim process model, PATH hive, NTFS directory checks,
  real MSI/EXE runs, ARM64 native runs, A17 timings) is verified only on the
  Windows CI runners.
