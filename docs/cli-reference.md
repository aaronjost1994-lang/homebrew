# WinBrew CLI reference (`wbrew`)

Applies to WinBrew v1.0 and the v1.1 installer additions. Every command
supports `--help`. Output rules are at the end of this page; exit codes are in
[Exit codes](#exit-codes).

## Global options

| Option | Meaning |
|---|---|
| `--json` | Exactly one JSON document on stdout (`schemas/cli-output.v1.schema.json`). |
| `--no-color` | No color. `NO_COLOR` and `color=never` do the same. |
| `--non-interactive` | Never prompt; a missing consent exits **10**. Also on with `interactive=never`, `WINBREW_NONINTERACTIVE=1`, or when stdin is not a terminal. |
| `--yes` | Approves the displayed **operation plan** only. It never implies `--trust-root`, `--modify-path`, `--allow-installer` or `--scope machine`. |
| `--verbose` / `--quiet` | More / less diagnostics on stderr. |
| `--offline` | Use only verified cache and metadata; never refresh (A15). |
| `--version` | Print the version (fast path: no DI container, no state access). |

## Commands

| Command | What it does | Locks |
|---|---|---|
| `search <query>` | Case-insensitive prefix/substring match over id, display name and description; ordered by id. Warns on expired-but-once-valid metadata. | no |
| `info <package>` | Catalog summary, versions, latest for this host, installed state, `writesToInstallDir` warning (from the signed manifest). | no |
| `install <package>[@<version>] [--version <v>] [--dry-run] [--allow-installer] [--scope user\|machine]` | Resolves the full graph from the catalog (PubGrub), shows the plan, installs it as one journaled operation. `@<version>` together with `--version` is exit 2. MSI/EXE packages need `--allow-installer` (v1.1). | yes |
| `uninstall <package> [--dry-run] [--allow-installer]` | Removes a package; refused (6) while another installed package depends on it. Installer packages run their recorded uninstaller. | yes |
| `autoremove [--dry-run]` | Removes dependency-only packages that nothing references any more. | yes |
| `list` | Installed packages (portable and, v1.1, installer-managed with reconciliation). | no |
| `outdated` | Installed packages with newer catalog versions. | no |
| `update` | Refreshes repository metadata (root → timestamp → index); never changes installed versions. | yes |
| `upgrade [package] [--dry-run]` | Highest satisfying versions (every unpinned package when none is named). | yes |
| `pin <package>` / `unpin <package>` | Fix / release an installed package's version. | yes |
| `cleanup [--dry-run]` | Trims `cache\artifacts\` to `cache.maxBytes`; never removes active, pinned, referenced or retained artifacts. | yes |
| `doctor` | Read-only environment report; exit 1 when a check fails. | no |
| `recover [--dry-run]` | Finishes or compensates interrupted operations from the journal. | yes |
| `repo add <name> <url> [--trust-root <fp>...]` | Downloads `root.json`, shows the root key fingerprints, requires approval, then performs the first verified refresh. | yes |
| `repo list` / `repo remove <name>` | Trust state per repository / removal (refused while its packages are installed). | no / yes |
| `bundle export --file <path> [--lock <path>]` | Bundle of the explicit packages + lockfile of everything installed. | no |
| `bundle install --file <path> [--lock <path>] [--locked]` | Resolve and install a bundle, or (`--locked`) exactly its lockfile. Default lock path: `dev.json` → `dev.lock.json`. | yes |
| `config get <key>` / `config set <key> <value>` / `config list` | D10 configuration. Unknown keys exit 2; release builds reject `testing.*`. | set: yes |
| `self install [--modify-path]` | Copies the release files into `app\<ver>\`, creates `bin\wbrew.exe`, offers the PATH change. | yes |
| `self update [--dry-run]` | Updates from the signed release feed; exit 3 "self-update not configured for this build" when the build has no feed. | yes |
| `self uninstall [--keep-data]` | Removes the PATH entry and shims, and (unless `--keep-data`) packages, cache and state. Refused while installer-managed packages remain. | yes |

### Consent summary (A8)

| Consent | Flag | Missing in a non-interactive session |
|---|---|---|
| The operation plan | `--yes` | exit 10 |
| A repository's root keys | `--trust-root <fingerprint>` (repeatable; every value must be a root key and together they must reach the root threshold) | exit 10 |
| Changing the user PATH | `--modify-path` | PATH unchanged, warning |
| Running an MSI/EXE (v1.1) | `--allow-installer` (required even interactively) | exit 10 |
| Per-machine operation (v1.1) | `--scope machine` (UAC prompt through the elevated helper) | per-user |

## Exit codes

The mapping lives in one place, `src/WinBrew.Cli/ExitCodes.cs`.

| Code | Name | Meaning |
|---|---|---|
| 0 | `Success` | Operation completed |
| 1 | `GeneralFailure` | Unexpected or unclassified failure (also: a failing `doctor` check; an installer failure such as msiexec 1603, with its log path) |
| 2 | `UsageError` | Unknown command, invalid arguments, invalid input, ambiguous package ID |
| 3 | `NotFound` | Package, version, repository or config key not found; no verified catalog; self-update not configured |
| 4 | `NetworkFailure` | Download or metadata fetch failed after retries (also: `--offline` with nothing cached) |
| 5 | `VerificationFailure` | Checksum, signature, expiry, rollback or archive-safety failure |
| 6 | `ConflictOrRefused` | Unresolvable dependencies, shim collision, dependent prevents uninstall, pinned |
| 7 | `LockUnavailable` | Another WinBrew process holds the mutation lock |
| 8 | `PartialOrRecoveryRequired` | Incomplete operation, failed recovery, or an ambiguous installer outcome (state `Unknown`) |
| 9 | `RebootRequired` | Installer completed and a reboot is required (msiexec 3010) or was started (1641) |
| 10 | `ConsentRequired` | A prompt would be needed but the session is non-interactive |
| 11 | `FilesInUse` | Files are locked by running processes (named in the output) |
| 12 | `InsufficientSpace` | Disk full or quota exceeded |
| 13 | `InstallerBusy` | Another MSI installation is in progress (msiexec **1618**) and the retries (4, with 5/10/20/40 s backoff) were exhausted |
| 130 | `Cancelled` | Ctrl+C, cancellation, a declined prompt, or msiexec 1602 |

**Note on 130.** 130 is the POSIX convention for "terminated by Ctrl+C"
(128 + SIGINT). Windows conventionally reports **`0xC000013A`**
(`STATUS_CONTROL_C_EXIT`, −1073741510) for a process ended by Ctrl+C.
WinBrew deliberately returns **130** when it handles cancellation itself,
for scripting ergonomics across shells — a documented deviation (ADR-0021).
A command run *through a shim* returns the child's own exit code
unchanged, so a child killed by Ctrl+C may still report `0xC000013A`.

### msiexec / EXE exit codes (v1.1)

| Installer exit | WinBrew exit |
|---|---|
| 0 | 0 |
| 3010 (reboot required), 1641 (reboot started) | 9 (the install is recorded first) |
| 1602 (user cancelled) | 130 |
| 1618 (another installation in progress) | retried, then 13 |
| anything else (e.g. 1603) | 1, with the log path |
| success but the result cannot be confirmed | 8 (state `Unknown`) |

## Output rules (A8)

- With `--json`, stdout carries exactly one document:
  `{ "schemaVersion": "1", "command": "list", "ok": true, "data": { }, "warnings": [ ] }`;
  on failure `"ok": false, "error": { "code": 6, "name": "ConflictOrRefused", "message": "…", "details": { } }`.
  The schema is `schemas/cli-output.v1.schema.json`, versioned separately
  from the manifest schema; `EnvelopeSchemaTests` validate it.
- Progress, color and decoration go only to an interactive terminal, never to
  stdout with `--json` or when stdout is redirected.
- Diagnostics go to stderr and `logs\`.
- `--dry-run` prints the full plan from the existing catalog and changes
  nothing: no downloads, no state writes, no shims, no metadata refresh.

## PowerShell completion

Dot-source `scripts/wbrew-completion.ps1` (shipped next to `wbrew.exe` in
future packaging; copy it today) from your profile. It uses System.CommandLine's
`[suggest:<position>]` directive, so completions always match the installed
version's command tree.
