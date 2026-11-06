# WinBrew user manual

WinBrew (`wbrew`) is a per-user package manager for Windows 10/11 (x64 and
ARM64). It installs command-line tools from signed repositories into your
profile, with no administrator rights. Every command you install is reachable
through one `bin\` folder on your `PATH`.

> **Status.** No public release exists yet. See
> [release-readiness.md](release-readiness.md) for what is missing. Until a
> release is published, build WinBrew from source (section 1.2).

Contents

1. [Install WinBrew](#1-install-winbrew)
2. [First steps](#2-first-steps)
3. [Everyday use](#3-everyday-use)
4. [Repositories and trust](#4-repositories-and-trust)
5. [Bundles: reproduce a setup](#5-bundles-reproduce-a-setup)
6. [MSI / EXE installer packages (v1.1)](#6-msi--exe-installer-packages-v11)
7. [Configuration](#7-configuration)
8. [Scripting and automation](#8-scripting-and-automation)
9. [Updating and removing WinBrew](#9-updating-and-removing-winbrew)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Install WinBrew

### Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11, on x64 or ARM64.
- No administrator rights. They are needed only for per-machine MSI/EXE packages (section 6).
- No .NET installation. `wbrew.exe` is self-contained.

### 1.1 From a release zip (recommended once releases exist)

1. Download two files from the release page: `wbrew-<version>-win-x64.zip` (or
   `-win-arm64.zip` on ARM devices) and `SHA256SUMS.txt`.
2. Verify the download:

   ```powershell
   (Get-FileHash .\wbrew-<version>-win-x64.zip -Algorithm SHA256).Hash
   # compare with the matching line in SHA256SUMS.txt
   ```

   A release labeled `UNSIGNED` has binaries without an Authenticode
   signature. Use one only if you accept that.
3. Extract the zip anywhere, for example `Downloads\wbrew`. It contains
   `wbrew.exe`, `e_sqlite3.dll`, `wbrew-shim.exe` and
   `WinBrew.ElevatedHelper.exe`. Nothing is extracted to `%TEMP%` at run time.
4. Install it into your profile:

   ```powershell
   .\wbrew.exe self install --modify-path
   ```

   This copies the files to `%LOCALAPPDATA%\WinBrew\app\<version>\`, creates
   `%LOCALAPPDATA%\WinBrew\bin\wbrew.exe` and, because of `--modify-path`,
   adds `bin\` to your user `PATH` in HKCU. Without `--modify-path`, WinBrew
   prints the folder and you add it yourself.
5. Open a **new** terminal and check the installation:

   ```powershell
   wbrew --version
   wbrew doctor
   ```

You can delete the extracted folder afterwards.

### 1.2 From source

You need the .NET SDK pinned in `global.json` (10.0.403) on Windows. The
Native AOT shim also needs the "Desktop development with C++" workload of
Visual Studio Build Tools.

```powershell
git clone <this repository> winbrew; cd winbrew
dotnet restore WinBrew.sln --locked-mode
dotnet build WinBrew.sln -c Release          # warnings are errors
dotnet test WinBrew.sln -c Release           # optional

# Produce the same zip a release would (win-x64 or win-arm64):
pwsh scripts/package-release.ps1 -Version 0.1.0-local -Output out -Rids win-x64
Expand-Archive out\wbrew-0.1.0-local-win-x64.zip -DestinationPath out\wbrew
out\wbrew\wbrew.exe self install --modify-path
```

On Linux or macOS you can build and run the tests, and `docker build --target
test .` runs both test passes. Installing packages needs Windows.

### 1.3 Where things go

Everything lives under `%LOCALAPPDATA%\WinBrew\`. Set `WINBREW_ROOT` to use
another folder.

| Folder | What it holds |
|---|---|
| `app\<version>\` | WinBrew itself, one folder per version |
| `bin\` | One small shim `.exe` per installed command, plus `wbrew.exe` |
| `packages\<id>\<version>\<arch>\` | Installed portable packages |
| `cache\` | Verified downloads and metadata |
| `state\` | The SQLite database, the lock file and backups |
| `logs\` | Diagnostics and installer logs |
| `config.json` | Your settings (section 7) |

---

## 2. First steps

### 2.1 Add a repository

Packages come from **repositories**. A repository is signed with root keys,
and you approve those keys once, when you add it. Get the repository's root
key fingerprint from its owner through a channel you trust (its website or
README), then run:

```powershell
wbrew repo add main https://<repository-url> --trust-root <fingerprint>
```

Without `--trust-root`, WinBrew shows the fingerprints and asks before it
trusts them. After approval it downloads and verifies the catalog.

### 2.2 Find and install a package

```powershell
wbrew search ripgrep            # substring/prefix match on id, name, description
wbrew info ripgrep              # versions, latest for this machine, install state
wbrew install ripgrep           # shows the full plan (with dependencies), asks to proceed
wbrew install ripgrep@14.1.0    # a specific version
wbrew install ripgrep --dry-run # print the plan; change nothing
```

The command is available right away in any terminal that has `bin\` on its
`PATH`:

```powershell
rg --version
```

---

## 3. Everyday use

| Task | Command |
|---|---|
| Refresh repository metadata (never changes installed versions) | `wbrew update` |
| See what is installed | `wbrew list` |
| See what has a newer version | `wbrew outdated` |
| Upgrade everything (except pinned packages) | `wbrew upgrade` |
| Upgrade one package | `wbrew upgrade <package>` |
| Keep a package at its current version | `wbrew pin <package>` / `wbrew unpin <package>` |
| Remove a package | `wbrew uninstall <package>` |
| Remove dependencies nothing needs any more | `wbrew autoremove` |
| Trim the download cache | `wbrew cleanup` |
| Check the environment | `wbrew doctor` |

Commands that change something show their plan first. They accept
`--dry-run` to preview and `--yes` to approve the plan without a prompt.
`--yes` approves **only** the plan: it never trusts a new key, edits your
`PATH`, runs an installer or elevates.

**How shims work.** `bin\rg.exe` is a small native launcher. It starts the
real `rg.exe` from `packages\`, passes your arguments through unchanged and
returns its exit code. Ctrl+C reaches the program as usual.

**Offline.** `--offline` uses only verified cached metadata and downloads.
If something isn't cached, the command fails with exit 4 and never fetches
it.

---

## 4. Repositories and trust

```powershell
wbrew repo list            # each repository, its trusted root version and freshness
wbrew repo remove <name>   # refused while packages from it are installed
```

What WinBrew verifies on every `update`:

- The **root** (`root.json`) signs the other roles. A key rotation must be
  signed by both the old and the new keys.
- The **timestamp** binds the exact length and SHA-256 of the **index**.
- Every package download must match the SHA-256 in its signed manifest.
- Expired metadata, rollback to older versions, and archives with dangerous
  paths are rejected (exit 5).

Details: [repository-trust-spec.md](repository-trust-spec.md),
[security-model.md](security-model.md). To publish your own packages, see
[package-author-guide.md](package-author-guide.md) and
[manifest-spec.md](manifest-spec.md).

---

## 5. Bundles: reproduce a setup

```powershell
wbrew bundle export --file dev.json                 # also writes dev.lock.json
wbrew bundle install --file dev.json                # resolve the newest matching versions
wbrew bundle install --file dev.json --locked       # install exactly what dev.lock.json records
```

Commit both files to a project so every machine gets the same tools.
Examples: [`examples/bundles/`](../examples/bundles/).

---

## 6. MSI / EXE installer packages (v1.1)

Some packages are traditional installers rather than portable zips. WinBrew
runs them only when you opt in:

```powershell
wbrew install <package> --allow-installer                  # per-user install
wbrew install <package> --allow-installer --scope machine  # per-machine: one UAC prompt
wbrew uninstall <package> --allow-installer
```

- Installer packages are installed one at a time, never as part of a larger plan.
- Per-machine operations go through `WinBrew.ElevatedHelper.exe`. It runs
  only the verified copy of the installer and refuses to touch anything it
  didn't install.
- `wbrew list` and `wbrew doctor` report when an installer package was
  changed or removed outside WinBrew. They report it and change nothing.
- Exit 9 means a reboot is required. Exit 13 means another MSI installation
  was still running after the retries.

---

## 7. Configuration

```powershell
wbrew config list
wbrew config get cache.maxBytes
wbrew config set retainVersions 2
```

| Key | Default | Meaning |
|---|---|---|
| `color` | `auto` | `auto`, `always` or `never` (`NO_COLOR` is honored too) |
| `interactive` | `auto` | `auto` or `never` (never prompt) |
| `cache.maxBytes` | 5 GiB | Target size for `wbrew cleanup` |
| `download.timeoutSeconds` | 300 | Per-download timeout |
| `download.maxBytes` | 4 GiB | Largest artifact accepted |
| `download.retries` | 3 | Retries for transient network errors |
| `retainVersions` | 1 | Inactive versions kept after an upgrade, for rollback |
| `lock.timeoutSeconds` | 30 | How long to wait for another WinBrew process |
| `trust.maxClockSkewSeconds` | 300 | Clock tolerance for metadata expiry |
| `trust.maxMetadataBytes` | 64 MiB | Largest metadata file accepted |
| `archive.maxEntries` / `archive.maxTotalBytes` / `archive.maxCompressionRatio` | 100000 / 8 GiB / 200 | Limits that protect against archive bombs |
| `path.managed` | `false` | Becomes `true` when you consent to the `PATH` change (`--modify-path`) |

Unknown keys are rejected with exit 2.

---

## 8. Scripting and automation

```powershell
wbrew list --json                                   # exactly one JSON document on stdout
wbrew upgrade --yes --non-interactive               # CI-friendly; missing consent exits 10
if ($LASTEXITCODE -eq 7) { 'another wbrew is running' }
```

- JSON output follows `schemas/cli-output.v1.schema.json`. Progress and
  diagnostics go to stderr.
- Non-interactive mode also turns on with `WINBREW_NONINTERACTIVE=1`, with
  `interactive=never`, or when stdin is not a terminal.
- Exit codes: 0 success · 1 general failure · 2 usage error · 3 not found ·
  4 network · 5 verification · 6 conflict/refused · 7 lock busy ·
  8 recovery required · 9 reboot required · 10 consent required ·
  11 files in use · 12 disk full · 13 installer busy · 130 cancelled.
  Full table: [cli-reference.md](cli-reference.md#exit-codes).
- PowerShell tab completion: dot-source `scripts/wbrew-completion.ps1` from
  your `$PROFILE`.

---

## 9. Updating and removing WinBrew

```powershell
wbrew self update --dry-run   # check the signed release feed
wbrew self update             # install the new version next to the old one, then switch
wbrew self uninstall          # remove the PATH entry, shims, packages, cache and state
wbrew self uninstall --keep-data   # remove WinBrew but keep packages/cache/state
```

`self uninstall` is refused while MSI/EXE packages are still installed.
Uninstall those first with `wbrew uninstall <package> --allow-installer`.
A build without a configured release feed reports exit 3 for `self update`.

---

## 10. Troubleshooting

| Symptom | What to do |
|---|---|
| `wbrew` not found after install | Open a new terminal. Check that `%LOCALAPPDATA%\WinBrew\bin` is on your user `PATH`. |
| Exit 7, "lock unavailable" | Another `wbrew` is running. Wait for it, or raise `lock.timeoutSeconds`. |
| Exit 8, "recovery required" | An operation was interrupted (crash, power loss). Run `wbrew recover`; it finishes or undoes the operation. |
| Exit 5, verification failure | A download or metadata did not match its signature or hash. Run `wbrew update`. If the failure persists, tell the repository owner. Never bypass it. |
| Exit 11, files in use | Close the programs named in the message, then retry. |
| Exit 6 on uninstall | Another installed package depends on it. The message names the dependent package. |
| A command runs the wrong version | Run `wbrew list` and `wbrew doctor`. `doctor` checks shims, PATH, state and installer packages. |

To report a problem, include the output of `wbrew doctor --json` and the
newest file in `%LOCALAPPDATA%\WinBrew\logs\`. More detail:
[recovery-troubleshooting.md](recovery-troubleshooting.md) and
[known-limitations.md](known-limitations.md).
