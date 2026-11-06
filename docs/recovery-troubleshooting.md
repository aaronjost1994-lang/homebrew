# Recovery and troubleshooting

## Interrupted operations

Every install, upgrade, uninstall, self install and self update is a journaled
operation. If WinBrew is killed, crashes or loses power:

- The **next mutating command** (or `wbrew recover`) finishes it: before the
  `StateCommitted` step it is compensated (previous versions and shims are
  restored), after it it is rolled forward.
- Read-only commands (`list`, `info`, `search`, `outdated`) never repair; they
  warn: "N interrupted operation(s) need recovery".
- `wbrew recover --dry-run` shows what would happen.

If recovery cannot reach a consistent state the operation ends in
**`RecoveryFailed`**, every mutating command exits **8**, and `wbrew doctor`
prints the exact inconsistency. WinBrew does not guess. Fix the reported
item (usually a file locked by antivirus or a running program), then run
`wbrew recover` again.

## Common errors

| Exit | Symptom | What to do |
|---|---|---|
| 3 | "No repository is configured" / package not found | `wbrew repo add <name> <url> --trust-root <fp>`; `wbrew update` |
| 4 | Download failed after retries | Check connectivity/proxy; `download.timeoutSeconds`, `download.retries` |
| 5 | Signature, checksum, expiry or rollback failure | Do **not** work around it. `wbrew update`; if it persists, the repository or the network is serving bad metadata — contact the publisher |
| 5 | "metadata expired … --dry-run never refreshes it" | Run `wbrew update` (or the real command, which refreshes) |
| 6 | Conflict explanation / pinned / dependents | Read the numbered explanation; `wbrew unpin`, uninstall dependents, or pick other versions |
| 7 | Another WinBrew process holds the lock | Wait; `lock.timeoutSeconds`; `doctor` shows the holder PID |
| 8 | Interrupted / RecoveryFailed / installer state Unknown | `wbrew recover`; `wbrew doctor`; for installers check the log path in the message |
| 9 | Reboot required after an MSI/EXE | Reboot |
| 10 | Consent required | Re-run with `--yes` (plan), `--trust-root`, `--allow-installer`, or interactively |
| 11 | Files in use | Close the named processes; WinBrew never terminates them |
| 12 | Disk full | Free space; `wbrew cleanup` |
| 13 | Another MSI installation in progress | Wait for Windows Update / other installers, retry |

## Shims

- A command prints `wbrew-shim: refusing target '…': it is not inside '…'` (or "is a reparse point") and exits 9009:
  its sidecar was modified; reinstall the package.
- `wbrew doctor` reports shims built from an old template after a self update
  that was rolled back: harmless (every template copy is a valid shim); the
  next install/upgrade replaces them.

## Offline use

`--offline` uses only the verified cache. Expired metadata is used offline
only if it was fully verified while valid, with a warning showing its age.
Locked bundles restore offline from cache (`bundle install --locked --offline`).

## Installer packages (v1.1)

- `list` shows `DRIFT` when Windows Installer or the detection rules disagree
  with WinBrew's record (e.g. the product was uninstalled from Settings).
  WinBrew never corrects this silently: uninstall it with
  `wbrew uninstall <pkg> --allow-installer` or reinstall it.
- "Refusing to use '%ProgramData%\WinBrew' …": the directory existed and was
  not created by WinBrew's helper (or grants others write access, or is a
  junction). As an administrator, inspect it, remove it, retry.

## Collecting diagnostics

`wbrew doctor --json`, the files in `logs\`, and the exact command with
`--verbose`. Never post `config.json` credentials (there should be none) or
signed download URLs.
