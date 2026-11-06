# Package author guide

This guide is for people who publish packages in a WinBrew repository. The
exact rules are in `manifest-spec.md` and `repository-trust-spec.md`.

## 1. Choose the package type

- **Portable (v1.0):** a ZIP that runs from any directory. WinBrew extracts it
  to `packages\<id>\<version>\<arch>\` and creates a shim per declared
  `.exe`. No scripts or hooks run, ever.
- **MSI / EXE (v1.1):** for software that must be installed. Users must pass
  `--allow-installer`; per-machine installs also `--scope machine` (UAC).
  Installer packages cannot have dependencies, and nothing can depend on
  them.

## 2. Build the ZIP

- Put every file under the archive root or one folder; executables are named
  by relative paths (`bin/tool.exe`).
- WinBrew rejects archives with absolute paths, drive letters, `..`,
  backslash/slash ambiguity, device names, `:` (alternate streams), trailing
  dots/spaces, case-insensitive duplicates, symlink entries, more than
  `archive.maxEntries` entries, more than `archive.maxTotalBytes`, or a
  compression ratio above `archive.maxCompressionRatio` (per entry and total,
  measured on the actual bytes).
- Ship native builds for each architecture you support. On ARM64 hosts WinBrew
  falls back to x64, then x86; on x64 hosts to x86 — and says so.
- Only `.exe` entry points get shims (no `.cmd`, `.bat`, `.ps1`, `.jar`).

## 3. State, upgrades and the missing `persist`

WinBrew deliberately has **no `persist` directories** (A3): unlike Scoop it
does not junction data directories from one version into the next. Each
version is a fresh directory; after an upgrade the previous one is kept only
for recovery (`retainVersions`) and then deleted.

Consequences for your package:

- An application that writes configuration or state **inside its own
  installation directory** loses it on every upgrade (and on uninstall).
- Store user data under `%APPDATA%`, `%LOCALAPPDATA%` or `%USERPROFILE%`
  instead — portable builds that offer a "portable mode" (data next to the
  exe) should be packaged with that mode off.
- If the application cannot be configured that way, set
  **`"writesToInstallDir": true`** in the manifest. `wbrew info` and
  `wbrew install` then warn users that its state is not carried across
  upgrades. It changes nothing else.

## 4. Write the manifest

- `version` must be normalized (`1.2.0`, not `1.2`); `vendorVersion` may hold
  the upstream spelling.
- Ranges use NuGet interval notation only: `[1.0.0,2.0.0)`, `[1.2.3]`. A bare
  `1.2.3` is rejected.
- Dependencies without a repository prefix mean "this repository"; use
  `<repo>/<id>` for another repository (users must have it configured).
- The `dependencies` and the artifact architectures in the manifest must be
  **identical** to the index entry, or installs fail with exit 5.
- Never publish a URL or checksum you did not build and hash yourself.

## 5. MSI and EXE specifics (v1.1)

- MSI: declare the `productCode` (and `upgradeCode`). If the package supports
  per-user installs, declare `userProperties` — the property set it was
  authored for (typically `ALLUSERS=2`, `MSIINSTALLPERUSER=1`). Do not put
  `ALLUSERS`/`MSIINSTALLPERUSER`/`REBOOT*` in `properties`.
- EXE: declare explicit `silentInstallArgs`, a `silentUninstall` executable
  (path pattern + arguments) and `detection` rules that are all true after a
  successful install and all false after uninstall. WinBrew never runs a
  registry `UninstallString` and never uses a shell.

## 6. Publish

1. Upload artifacts and manifests (`manifests/<id>/<version>.json`; keep old
   ones forever).
2. Regenerate `index.json` with a higher `version`, sign it with the index
   key(s).
3. Write a new `timestamp.json` (higher version, ≤ 7 days) binding the new
   index's length and SHA-256; sign it with the timestamp key.
4. Re-sign the timestamp before it expires even when nothing changed —
   clients refuse expired metadata online and warn offline.

`scripts/new-fixture-repo.ps1` shows the exact format with test keys.
