# ADR-0012: MSI/EXE installers and the elevated helper (v1.1, A12)

**Status:** accepted (Phase E, 2026-11-06)

## Decisions

1. **Typed installer declarations** (`manifest.installer`) are
   executable-code trust decisions. `msi { productCode, upgradeCode?,
   properties, userProperties }`; `exe { silentInstallArgs, silentUninstall
   { path, args }, detection { uninstallKey?, file { path, version? }? } }`.
   Paths are patterns rooted at `{ProgramFiles}`, `{ProgramFilesX86}`,
   `{ProgramData}`, `{LocalAppData}`, `{AppData}` (no traversal, drive
   letters, device names or `:`); a machine-only package must use a machine
   root. Switches are never guessed; registry `UninstallString` values are
   never run.
2. **Scope.** Per-user only when the manifest declares `user` AND the
   per-user property set it was authored for (`userProperties`, e.g.
   `ALLUSERS=2 MSIINSTALLPERUSER=1`). `ALLUSERS`, `MSIINSTALLPERUSER`,
   `REBOOT`, `REBOOTPROMPT` cannot appear in `properties`. Property values use
   a conservative alphabet (no spaces, quotes or control characters) so they
   reach msiexec unchanged through `ArgumentList`. After an install the
   actual context is read with `MsiGetProductInfoEx` and recorded (a
   different context than requested is recorded and warned about).
3. **Consent.** `--allow-installer` for install AND uninstall (exit 10
   without it, checked before any download); `--scope machine` selects the
   elevated helper. `--yes` implies neither.
4. **Exit codes** (`MsiRules`): 0 → 0; 3010/1641 → 9 (the install is
   recorded first, state `RebootRequired`); 1602 → 130; 1618 → retried 4
   times with 5/10/20/40 s backoff, then 13 `InstallerBusy`; anything else
   (1603 …) → 1 with the log path. EXE: 0, 3010/1641; anything else → 1.
   Unconfirmable results (MSI context absent, EXE detection rules disagree)
   → state `Unknown`, exit 8. Rollback is not promised.
5. **Storage.** Schema migration v3 adds `installer_packages` (repo, id,
   version, arch, type, scope, ProductCode, UpgradeCode, state, log, the
   declaration JSON). It is separate from `installed_packages` (portable
   packages: shims, dependency edges, retention). Per-machine installs are
   recorded in the installing user's database and reconciled through the
   ProductCode; other users' WinBrew instances do not see them.
6. **v1.1 limitation:** installer packages have no dependencies (validator)
   and are installed alone — a plan that would change an installer package
   together with anything else is refused (exit 6). `upgrade` does not touch
   installer packages.
7. **Reconciliation is explicit:** `list` (row `drift` + a warning) and
   `doctor` (`installers.reconcile`) query `MsiQueryProductState` / the
   detection rules and REPORT drift; nothing is corrected silently.
8. **Per-user runs** use a staging copy opened with `FileShare.Read` only,
   hashed from that handle and kept open while msiexec/the EXE runs.
9. **Elevated helper** (`WinBrew.ElevatedHelper.exe`, requireAdministrator,
   started with the `runas` verb once per operation — the only
   `UseShellExecute = true` in WinBrew, one argument through `ArgumentList`).
   Its sole argument is `staging\elevated\request-<32 hex>.json`; the request
   (`ElevatedRequest`) is strictly validated untrusted input with four typed
   operations and no command line. It trusts `%ProgramData%\WinBrew` and
   `\elevated` only after checking — on a handle opened with
   `FILE_FLAG_OPEN_REPARSE_POINT` — every path component for reparse points
   and the directory's owner (Administrators/SYSTEM) and DACL (no other
   principal with write/append/delete/WRITE_DAC/WRITE_OWNER, generic rights,
   inherited or inherit-only ACEs included; `DirectoryTrustRules`). Missing
   directories are created with `CreateDirectoryW` carrying the final
   descriptor (owner Administrators, protected DACL: Administrators + SYSTEM).
   `ERROR_ALREADY_EXISTS` re-runs the checks; nothing is ever adopted,
   repaired or re-ACLed. The `<op>` directory name is random and generated
   by the helper; the artifact is copied in (source opened `FileShare.Read`,
   destination `CreateNew`), opened `FileShare.Read`, hashed from that handle,
   compared with the request, and held open while the installer runs. The
   helper writes `request-….result.json` (exit code, log copied to
   `request-….log`, ProductCode) and deletes `<op>`.
10. **No shell strings** — enforced by `NoShellCommandStringTests`, which
    scans `src/` for `.Arguments =`, `UseShellExecute = true`, `cmd /c`,
    `powershell -` and `ShellExecuteEx(` and allows only the runas launch.

## Consequences and remaining risk

- Malware already running as the same user can influence what that user
  approves at a UAC prompt (and can tamper with the request before the
  helper starts); the helper limits the damage to the four typed operations
  on a hash-verified artifact.
- Real MSI/EXE runs, the NTFS directory attacks and ACL snapshots run only on
  disposable Windows runners (`InstallerWindowsTests`, Phase F CI).
- The release zip must also ship `WinBrew.ElevatedHelper.exe` next to
  `wbrew.exe` (Phase F packaging); without it per-machine operations fail
  with a clear error.
