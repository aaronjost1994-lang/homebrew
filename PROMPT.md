# WinBrew — .NET Native Windows Package Manager
# Implementation Prompt v2.3 — PROMPT.md

**Prompt version:** 2.3.1 (2026-11-03)
**Supersedes:** v2.3. v2.3.1 is a planning-consistency patch: the
phase order vs D1 clarified, `self uninstall` and catalog `info`
assigned to phases, the `phases/` layout adopted, the bundle
`--locked` syntax defined, the elevated-helper request transport
specified, and two Phase 0 additions (a System.CommandLine 2.0
stable probe; per-project target frameworks). No design changes.
v2.3 applies every finding
from "Review: WinBrew .NET Implementation Prompt v2.2" (4 critical,
7 important, 7 minor) and splits the prompt into session-sized files.
**Where the rest went:** the changelogs, the v2.1 correction and the
review traceability appendices are in `CHANGES.md` (never sent to
implementation sessions); the reasoning behind each decision is in
`RATIONALE.md` (sent with Phase 0 and Phase F only).

## How to use this file set

| File | Contents | Sent to |
|---|---|---|
| **`PROMPT.md`** (this file) | Part A — the design (A1–A19); Part C — report templates | **Every** session |
| **`phases/phase-<x>.md`** | One Part B phase prompt each (0, A–F) | **Its own session only** |
| `CHANGES.md` | Changelogs, review verification, traceability | Never |
| `RATIONALE.md` | Why each decision was made | Phase 0 and Phase F only |

Start each session with `PROMPT.md`, exactly **one** phase file and the
checkpoint report from the previous phase. Run the phases in order:
0, A, B, C, D, E, F (D1 explains why E precedes F). Do
not ask for more than one phase in a single session.

# PART A — SHARED PREAMBLE

## A1. Role and objective

Act as a senior .NET engineer, Windows platform engineer and security
reviewer.

Build a complete, maintainable Windows CLI package manager called
**WinBrew**, with the executable name `wbrew`.

Conceptual references (no code or format compatibility is implied):
- Homebrew: https://github.com/homebrew/brew (UX and command vocabulary)
- Scoop: per-user portable installs and the shim model
- winget manifest schema: prior art for declaring installer metadata
- The Update Framework (TUF): repository trust model
- PubGrub: dependency resolution with explainable conflicts
- Cargo's sparse index: the `index.json` catalog model (D6)

WinBrew is an independent, Windows-native implementation. Do not assume
that Homebrew formulae, casks or bottles, or Scoop/winget manifests, work
with it. Do not claim affiliation with any of these projects. Preserve
applicable license notices if you reuse any code.

> **Naming note for the project owner:** other repositories named
> "WinBrew"/"winbrew" already exist. Check for trademark and name
> conflicts before publishing.

## A2. Execution environment and honesty rules

- **Expected environment:** an agent with shell access on Windows 11
  x64, the .NET 10 SDK, and network access to nuget.org. ARM64
  execution is expected only in CI on ARM64 runners.
- If you cannot run builds or tests in your environment, say so
  at the start of the session. Produce code, mark every check as
  **NOT RUN** with the reason, and give the exact commands to run.
- **Never** fabricate build output, test results, benchmark numbers,
  checksums, signatures or download URLs.
- **Never** substitute placeholders, `NotImplementedException`, TODO
  stubs or fake success paths on a critical execution path. If something
  cannot be finished in this phase, leave it out and list it in the
  checkpoint report.
- **Never** truncate source files. If context runs out, finish the
  current file, stop, and write the checkpoint report (Part C1).

## A3. Non-goals

Version 1.x will **not** include:

- A GUI, tray app, Windows service or background daemon.
- Multiple active versions of the same package (side-by-side).
- Persistent portable data directories across upgrades (Scoop-style
  `persist` directory junctions). Portable applications that write user
  configuration or state inside their own installation directory rather
  than `%APPDATA%`, `%LOCALAPPDATA%` or `%USERPROFILE%` will not carry
  that state across upgrades. The package-author guide documents this
  consequence for authors, and a package may declare
  `writesToInstallDir: true` (A9) so `wbrew info` shows a warning.
- Install, uninstall or upgrade scripts or hooks of any kind
  (PowerShell, batch, shell, JS).
- Shims for non-`.exe` entry points (`.cmd`, `.bat`, `.ps1`, `.vbs`,
  `.jar`).
- Building packages from source.
- Microsoft Store, MSIX/AppX or winget-source integration.
- Per-machine installation of **portable** packages.
- Dependencies on, or from, MSI/EXE installer packages.
- Telemetry of any kind.
- Automatic termination of running processes.

## A4. Technology requirements

- C# on **.NET 10 LTS**. Pin the latest patched `10.0.1xx` SDK available
  at implementation time in `global.json` with `"rollForward": "latestPatch"`,
  and record the exact version in the checkpoint.
- Target Windows 11 on **x64 and ARM64**.
- **Publish layout — no native self-extraction.** Self-contained,
  architecture-specific, single-file **managed** bundles
  (`win-x64`, `win-arm64`):
  - `PublishSingleFile=true`, `SelfContained=true`
  - `IncludeNativeLibrariesForSelfExtract=false` — the bundle contains
    **no native libraries**. `e_sqlite3.dll` ships **next to**
    `wbrew.exe` and is loaded from there. Nothing is ever extracted to
    `%TEMP%`, and no native-extraction configuration exists anywhere in
    WinBrew (rejected alternatives are recorded in `RATIONALE.md`).
  - `PublishTrimmed=false`, `PublishAot=false` for `wbrew`
    (`WinBrew.Shim` is the only AOT component, see D4).
  - `WinBrew.Shim` is published as a separate Native AOT executable,
    `wbrew-shim.exe`, for each RID (D4).
  - Release artifacts are `wbrew-<ver>-win-<arch>.zip` archives. Phase A
    records the exact publish output for each RID in the ADR and in the
    committed file `scripts/publish-files.txt`; the zip contains that
    set minus `.pdb` files, plus `wbrew-shim.exe` (A14, A18). A CI test
    fails if the publish output ever differs from that manifest.
- **Fast-path startup:** `--version` and `--help` are handled or
  short-circuited before the `IHost` is built or `Microsoft.Data.Sqlite`
  is initialized (`SQLitePCLRaw.Batteries_V2.Init()`). A test asserts
  that the published binaries never create an extraction directory
  under `%TEMP%`; the observed behavior is recorded in the ADR.
- **Lazy service construction:** command handlers get services through a
  factory that builds the host on first use. The DI container is never
  built during parsing, and never for fast-path commands.
- Microsoft.Extensions.Hosting for dependency injection, configuration
  and lifecycle. No hosted background services in v1.
- **System.CommandLine 2.0 stable API.** Do not use the pre-2.0 beta API
  (`SetHandler`, `AddOption`, `AddCommand`, `InvokeAsync` on beta
  types) that appears in older online examples. Anchor all CLI code to
  the 2.0 stable surface, with **lazy** service construction — parsing
  and fast-path commands never build the host:
  ```csharp
  // Program.cs — services are built lazily, on first use.
  var services = new Lazy<IServiceProvider>(() => ServiceConfigurator.BuildProvider());

  var root = new RootCommand("WinBrew package manager");
  var jsonOption = new Option<bool>("--json") { Recursive = true };
  root.Options.Add(jsonOption);

  var listCommand = new Command("list", "List installed packages");
  listCommand.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
  {
      bool json = parseResult.GetValue(jsonOption);
      var useCase = services.Value.GetRequiredService<ListUseCase>();
      return await useCase.ExecuteAsync(json, ct);   // return value = process exit code
  });
  root.Subcommands.Add(listCommand);

  return await root.Parse(args).InvokeAsync();
  ```
  - The action's return value is the process exit code (D9).
  - Exceptions are mapped **centrally** through `ExitCodes.cs`, never per
    handler.
  - Disable System.CommandLine's default exception handling so the D9
    mapping and the JSON error envelope stay authoritative.
- Spectre.Console for interactive output only.
- Microsoft.Data.Sqlite (with the bundled SQLitePCLRaw provider) and
  `System.Formats.Asn1` (SPKI parsing for D6 key validation).
- System.Text.Json with **source-generated** serializer contexts.
- NuGet.Versioning for versions and ranges.
- HttpClient through `IHttpClientFactory`, with bounded retries,
  timeouts and cancellation. Handle redirects manually (see A13).
- xUnit for all automated tests.
- Central package management (`Directory.Packages.props`) with exact
  pinned versions, plus `RestorePackagesWithLockFile=true` in
  `Directory.Build.props` (do not set `RestoreLockedMode=true` by
  default outside CI).
  - Set `<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>` in
    `WinBrew.Cli`, `WinBrew.Shim` (and `WinBrew.ElevatedHelper` in
    v1.1) so `packages.lock.json` includes runtime-specific packages.
    Regenerate the lock files whenever `RuntimeIdentifiers` changes.
    CI restores with `--locked-mode` for **every RID it publishes**.
  - Generate `packages.lock.json` via `dotnet restore --use-lock-file`
    when shell access is available. If running in an environment
    without shell/network access, **do not** fabricate
    `packages.lock.json` by hand; list `dotnet restore --use-lock-file`
    as a required step in the checkpoint report.
- `Nullable=enable`, `TreatWarningsAsErrors=true`,
  `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`.
- End users must not need Docker, WSL, Ruby, Python, Git or a
  separately installed .NET runtime.
- Cryptography uses only `System.Security.Cryptography` (plus
  `System.Formats.Asn1` for **parsing** key structures). Do not
  implement custom cryptographic algorithms.

## A5. Architecture and repository tree

```
WinBrew.sln
global.json
Directory.Build.props
Directory.Packages.props
.editorconfig
LICENSE
README.md
CHANGELOG.md
CONTRIBUTING.md
SECURITY.md
src/
  WinBrew.Cli/              # Parsing, output, progress, exit-code mapping
  WinBrew.Core/             # Domain types, constraints, graph, operation models
  WinBrew.Application/      # Use cases, planning, orchestration, interfaces
  WinBrew.Infrastructure/   # HTTP, repositories, SQLite, files, cache, config, trust
  WinBrew.Windows/          # PATH, shims mgmt, Restart Manager, MSI/EXE, elevation client
  WinBrew.Shim/             # Native AOT shim executable (see D4)
  WinBrew.ElevatedHelper/   # v1.1 only: narrowly scoped elevated helper (see A12)
tests/
  WinBrew.UnitTests/
  WinBrew.IntegrationTests/
  WinBrew.EndToEndTests/
  WinBrew.TestFixtures/     # Fixture package builder, local HTTP server, test signing keys
schemas/
  manifest.v1.schema.json
  repository-root.v1.schema.json
  repository-timestamp.v1.schema.json
  repository-index.v1.schema.json
  bundle.v1.schema.json
  lockfile.v1.schema.json
  config.v1.schema.json
  cli-output.v1.schema.json
examples/
  manifests/
  repository/               # Signed fixture repository (test keys only)
  bundles/
docs/
  architecture.md
  adr/
  cli-reference.md
  manifest-spec.md
  repository-spec.md
  package-author-guide.md
  security-model.md
  threat-model.md
  recovery-and-troubleshooting.md
  release-checklist.md
  known-limitations.md
scripts/
  build.ps1
  test.ps1
  publish.ps1
  publish-files.txt        # exact publish output per RID (A4, R13)
  new-fixture-repo.ps1
  wbrew-completion.ps1
.github/workflows/
  ci.yml
  release.yml
```

**Rules**
- Dependencies point inward: Cli → Application → Core.
  Infrastructure and Windows implement Application interfaces.
  Core has no I/O dependencies.
- Command handlers only parse input, call one use case and render the
  result. Business logic stays out of handlers.
- All I/O is async and takes a `CancellationToken`.
- Use an interface only where there is a real second implementation
  or a test seam. Avoid speculative abstractions.

## A6. Storage layout

Installations are per-user by default and need no administrator rights.

```
%LOCALAPPDATA%\WinBrew\
  config.json
  app\<wbrew-version>\wbrew.exe          # WinBrew itself (see A14)
  app\<wbrew-version>\e_sqlite3.dll      # native SQLite, shipped next to the bundle
  app\<wbrew-version>\wbrew-shim.exe     # shim template; copies of it live in bin\ (D4)
  packages\<package-id>\<version>\<arch>\
  bin\                                   # Shims only; the only directory added to PATH
  cache\artifacts\<sha256>               # Content-addressed artifacts
  cache\metadata\<repo>\                 # Verified repository metadata
  repositories\<repo>\trust\             # Trusted root history
  staging\<operation-id>\                # Same volume as packages\
  logs\
  state\winbrew.db
  state\winbrew.lock
  state\backups\
```

- The root can be overridden with `WINBREW_ROOT` (used by tests). The
  whole layout must stay on one volume so that renames are atomic.
  Detect and refuse any configuration that would put `staging\`,
  `packages\`, `cache\` or `bin\` on a different volume from the rest
  of the layout: verified artifacts and shims are renamed into place,
  so all four directories must share the volume with `state\`.
- Database: SQLite in WAL mode, `user_version` schema migrations, and a
  copy in `state\backups\` (via the SQLite backup API) before every
  migration. Never downgrade a schema. A newer schema version than the
  binary supports produces a clear error (exit 1).
- Folder redirection and roaming profiles are a known limitation and
  must be documented.

## A7. Locked design decisions

These decisions are fixed. If you believe one is wrong, implement it
anyway and raise the concern in the checkpoint report. Each decision
needs an ADR in `docs/adr/`.

### D1. Scope
- **v1.0 release candidate:** per-user scope, ZIP portable packages only
  (Phases A–D; release hardening in Phase F).
- **v1.1:** MSI (per-user and per-machine) and explicitly approved EXE
  installers (Phase E).
- **Phase order vs release:** the phases run 0, A–D, E, F, so Phase E
  (v1.1) precedes Phase F. The release prepared in Phase F is the
  first public release: its readiness report (C2) verifies the v1.0
  criteria (A19 items 1–12) **and** lists the v1.1 additions with
  their own evidence. A portable-only v1.0 build can still be cut by
  tagging the tree after Phase D; Phase F itself runs once, after E,
  and hardens both (RATIONALE.md, R17).
- WinBrew never changes installation scope silently. A per-machine
  operation needs `--scope machine`, explicit consent and UAC elevation
  through the helper.

### D2. Dependency resolver
- Implement **PubGrub** (incompatibility-based, with conflict-derivation
  explanations).
- **Input — the resolver is pure:** it consumes an in-memory snapshot
  built from the **SQLite catalog** (D6). Every candidate version's
  dependency ranges and artifact architectures come from the catalog
  snapshot, never from manifests fetched during resolution. The resolver
  performs no I/O and makes no machine changes. Dependency IDs are
  qualified per A9: an unqualified ID belongs to the declaring package's
  repository, so the plan never depends on which other repositories
  happen to be configured.
- Deterministic rules:
  1. Pinned packages are fixed at their installed version.
  2. `install`: prefer already-installed versions that satisfy all
     constraints. Otherwise pick the highest satisfying version.
  3. `upgrade`: pick the highest satisfying version.
  4. **Decision heuristic — two separate concerns:**
     - *Which package to decide next:* always run unit propagation to
       completion first. A package has **active constraints** when it
       appears with a positive term in the partial solution but has no
       decision yet. When unit propagation is exhausted and multiple
       undecided packages have active constraints, pick the next package
       in ordinal order of normalized ID (`<repo>/<id>`).
     - *Which version to choose:* rules 1–3 above.
  5. **Determinism is required; performance tuning is not.** A
     "fewest candidate versions" branching heuristic is allowed only if
     ties are still broken by ordinal `<repo>/<id>`.
  6. Prerelease versions are selected only when the requested
     `--version` is a prerelease or a range bound is a prerelease.
- **Explanations** must name repository-qualified IDs (`<repo>/<id>`)
  and the original range strings as written in the metadata.
- If it fails, it produces a human-readable explanation and a
  structured `conflict` object for `--json`. It makes no machine changes.

### D3. Versions and ranges
- `version` must parse as a NuGet.Versioning `NuGetVersion` and is
  normalized with `ToNormalizedString()`.
- `vendorVersion` is an optional, free-form, display-only string.
- Dependency ranges use **NuGet interval notation only**: `[1.0,2.0)`,
  `[1.2.3]`, `(,3.0)`. A bare `1.2.3` in a range is **rejected** by the
  validator because NuGet reads it as `>= 1.2.3`, which surprises people.

### D4. Shim model
- One shim executable per architecture, built from `WinBrew.Shim` with
  **Native AOT**. This is the only AOT component, and it gets its own
  compatibility tests on both architectures. The built executable,
  `wbrew-shim.exe`, ships in the release zip and is copied into
  `app\<ver>\` by `self install` and `self update` (A14). The template
  for every shim copy is `app\<active-ver>\wbrew-shim.exe`.
- Each command in `bin\` is a copy of that template (`<name>.exe`) plus
  a sidecar file (`<name>.shim.json`) containing the target path, the
  owning package ID and version, and the subsystem (console or GUI).
- **Sidecar containment.** The sidecar `target` must be an absolute
  path that resolves inside `<root>\packages\`. The only exception is
  the sidecar named `wbrew.shim.json` — the WinBrew self-shim — whose
  target must resolve inside `<root>\app\`. `<root>` is always the
  parent directory of the shim's own directory (`bin\..`); the shim
  never reads `WINBREW_ROOT` or any other environment variable for
  this. Containment normalizes the full path with a trailing separator
  and refuses a target that contains a reparse point anywhere along its
  path. On violation the shim writes one line to stderr and exits with
  9009. This check is defense in depth only: it does not protect
  against processes running as the same user, which can already write
  to `packages\` and `bin\`.
- **Argument contract:** the shim reads the raw command line via
  `GetCommandLineW()` and strips `argv[0]` using the **MSVC C-runtime
  rules** (an ADR records the parser choice, the algorithm and the
  observed behavior; `CommandLineToArgvW` is NOT the reference — it
  applies backslash escaping that the C runtime does not apply to
  `argv[0]`). In C-runtime semantics `"` toggles quoting anywhere
  inside `argv[0]`, and backslashes have no special meaning. The scan:
  ```
  inQuote = false
  i = 0
  while i < len(cmd):
      c = cmd[i]
      if c == '"':
          inQuote = !inQuote
      elif (c == ' ' or c == '\t') and not inQuote:
          break
      i = i + 1
  tail = cmd[i:]        # always empty, or begins with ' ' or '\t'
  ```
  - An empty command line (possible with a null `lpCommandLine`) is
    treated as an empty tail. A debug assertion (test-hooks builds
    only) checks that the tail is empty or starts with whitespace.
  - There is **no separator insertion**: characters immediately after
    a closing quote belong to `argv[0]` itself. For example
    `"x.exe"arg` means `argv[0]` is `x.exearg` and **no arguments are
    forwarded**; `C:\Pro"gram Fi"les\x.exe a` forwards ` a`.
  - The shim passes `"\"<targetPath>\"" + tail` as `lpCommandLine` to
    `CreateProcessW`. It never re-parses or re-quotes arguments.
  - **Length check:** before calling `CreateProcessW`, check the
    composed command line against the 32,767-character Windows limit.
    On overflow, write one line to stderr and exit with **206**
    (`ERROR_FILENAME_EXCED_RANGE`).
- The child inherits the current working directory, environment and
  standard handles.
- Console targets: the shim assigns the child to a Job Object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` **and
  `JOB_OBJECT_LIMIT_BREAKAWAY_OK`** (processes that explicitly break
  away — for example daemons started by CLIs — may outlive the shim;
  this is documented). If the child cannot be assigned to a job (for
  example the shim already runs inside an incompatible job), log it and
  continue without a job.
  **Ctrl+C:** the shim installs a console handler **routine that returns
  `TRUE`** (swallowing the event) so the child receives
  Ctrl+C/Ctrl+Break normally. It **never** calls
  `SetConsoleCtrlHandler(NULL, TRUE)`: that sets an inheritable ignore
  flag and would suppress Ctrl+C in the child too. The shim waits and
  returns the child's exit code.
- GUI targets (detected from the PE header at install time): the shim
  starts the target, does not wait, and returns 0 if the launch
  succeeded.
- If the target is missing, the shim writes one line to stderr and
  exits with code **9009**, matching cmd.exe's "not recognized" code.
- **Shim activation and live upgrades:**
  - Sidecar files (`<name>.shim.json`) are always updated via
    write-to-temp plus `File.Replace`/`File.Move(overwrite)`.
  - **Ownership is determined by the WinBrew database, never by the
    hash.** A shim binary built from an older template is still
    managed; a file in `bin\` with no database record is unmanaged and
    is never touched. The SHA-256 comparison against the current
    `app\<active-ver>\wbrew-shim.exe` template decides only whether
    `<name>.exe` needs replacing. `doctor` checks that the active
    template exists and matches the hash recorded at install time.
  - During package `upgrade` (or compensation), if `bin\<name>.exe`
    already exists and is a managed shim, WinBrew updates **only**
    `<name>.shim.json` and does not touch `<name>.exe` (preventing
    `ERROR_SHARING_VIOLATION` if the shimmed tool is currently
    running).
  - **The shim opens its sidecar with `FileShare.Read |
    FileShare.Delete`, reads it fully, and closes the handle before
    `CreateProcessW`**, so `File.Replace` on the sidecar succeeds even
    while other shim instances are running.
  - If `<name>.exe` itself must be updated while locked (e.g., after a
    `self update` that changed the shim binary), rename the running
    `bin\<name>.exe` to `bin\<name>.exe.old.<guid>` on the same
    volume, then place the new `bin\<name>.exe`. **Every
    `.old.<guid>` file is recorded in the operation journal. Cleanup
    deletes only recorded `.old` files, retries later if they are
    still locked, and never deletes `.old.*` files matched by pattern
    alone.**
  - **Replacing every shim after `self update` is one journaled bulk
    operation**, with one fault checkpoint per shim. It is idempotent
    and resumable by `recover`.

### D5. Interprocess locking
- A global mutation lock: `state\winbrew.lock` opened with
  `FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read` and held
  for the duration of every mutating operation, including recovery.
  Because every writer requests `FileAccess.ReadWrite` with
  `FileShare.Read`, the OS kernel prevents any second writer from
  acquiring the lock while allowing diagnostic readers.
- **Mutating commands include `update`, `repo add` and `repo remove`**
  (they change cached metadata and trust state). Only genuinely
  read-only commands (`search`, `info`, `list`, `outdated`,
  `config get`, `config list`, `doctor`) skip the lock and rely on
  SQLite WAL snapshot reads.
- Upon acquiring the lock, the holder truncates the file, writes a
  single-line UTF-8 JSON object
  (`{ "pid": 1234, "command": "install", "startedAt": "...", "processStartTime": "..." }`)
  and flushes it. `processStartTime` comes from `GetProcessTimes` and
  protects against PID reuse.
- **Torn reads:** a reader can observe the file mid-truncate or
  mid-write. Empty or unparseable metadata means "holder unknown" —
  keep waiting; never treat it as an error.
- **No stale-lock breaking.** If the holder dies, the OS closes its
  handle and the lock is free automatically; the next holder overwrites
  the metadata. WinBrew never implements "stale lock breaking".
- `doctor` reports a holder as running only if **both** the PID and the
  recorded process start time match the live process.
- Lock wait: `lock.timeoutSeconds` (default 30), polling by retrying
  the open with bounded exponential backoff and jitter — never a
  busy-loop. When it expires, the waiting process reads
  `state\winbrew.lock` (using `FileAccess.Read, FileShare.ReadWrite`)
  to include the blocking PID and command in the error message, and
  exits with 7. In non-interactive mode no prompt is shown.

### D6. Repository trust (simplified TUF) and catalog indexing
- Signatures: **ECDSA P-256 with SHA-256**, IEEE P1363 signature format,
  base64-encoded.
- **Key import and signature validation rules (.NET).** Do not rely on
  `ExportParameters(false).Curve.Oid.Value` after import: on Windows
  CNG it can be `null` with only a `FriendlyName` (`nistP256` /
  `ECDSA_P256`) set, so an `Oid.Value`-only check rejects valid keys or
  gets skipped by defensive coding. Instead:
  - **Before importing**, parse each SubjectPublicKeyInfo with
    `System.Formats.Asn1` and require:
    - algorithm OID `1.2.840.10045.2.1` (`id-ecPublicKey`);
    - parameters exactly the named-curve OID
      `1.2.840.10045.3.1.7` (explicit curve parameters are rejected);
    - an uncompressed point (`0x04` prefix, 65 bytes).
  - **After importing**, also assert `ECDsa.KeySize == 256`.
  - **Before verifying**, require each signature to be exactly **64
    bytes** (IEEE P1363 `r‖s`), and always specify
    `DSASignatureFormat.IeeeP1363FixedFieldConcatenation` in `SignData`
    and `VerifyData`.
  - Envelope validation rejects **duplicate key IDs** within one
    envelope, and each distinct key counts **at most once** toward the
    signature threshold.
- Key ID: lowercase hex SHA-256 of the DER-encoded SubjectPublicKeyInfo.
- Envelope: signatures cover the **exact payload bytes**, so no JSON
  canonicalization is needed:
  ```json
  {
    "payload": "<base64 of UTF-8 JSON bytes>",
    "payloadType": "application/vnd.winbrew.root+json;version=1",
    "signatures": [ { "keyid": "<hex>", "sig": "<base64>" } ]
  }
  ```
- Roles:

  | File | Signed by | Contents | Max expiry |
  |---|---|---|---|
  | `root.json` (+ `root/<N>.json` history) | root keys (threshold) | Keys and thresholds for all roles, `version`, `expires` | 365 days |
  | `timestamp.json` | timestamp key | `version`, `expires`, SHA-256 and length of `index.json` | 7 days |
  | `index.json` | index key(s) | `version`, `expires`, packages (summary metadata + versions → `{ sha256, size, architectures[], dependencies[] }`) | 30 days |
  | `manifests/<id>/<version>.json` | (none; bound by `index.json` digest) | Package manifest | n/a |

- **`index.json` is a sparse index (Cargo-style) and the only resolver
  input.** Each version entry carries the manifest digest (`sha256`),
  `size`, the artifact `architectures[]` for that version, and the
  manifest's `dependencies[]` verbatim. There is **no `latestVersion`
  field**: "latest" depends on architecture and prerelease rules and is
  computed locally from `versions`.
- **SQLite catalog:** when `repo add` or `update` verifies a new
  `index.json`, WinBrew ingests the package summaries and version
  entries (including dependencies and architectures) into catalog
  tables in `winbrew.db`. `wbrew search` **and the resolver (D2)** read
  **only** the catalog snapshot — large JSON files are never re-parsed
  per invocation. Catalog rows and the accepted role versions
  (timestamp/index high-water marks) are written in **one SQLite
  transaction**.
- **Metadata size limits:** `trust.maxMetadataBytes` (default 64 MiB,
  D10). While streaming `index.json`, enforce the `length` declared in
  `timestamp.json` and reject anything larger or shorter.
- **Search semantics:** prefix-or-substring match over a `search_text`
  column built with `ToLowerInvariant()` from `id`, `displayName` and
  `description` at ingest; the query is lowercased the same way.
  SQLite `LIKE` is case-insensitive only for ASCII, so case folding is
  done explicitly and never delegated to `LIKE`. Results are ordered by
  `id`. If the bundled SQLitePCLRaw build includes FTS5 (probed in
  Phase A and recorded), search MAY use an FTS5 index over the same
  `search_text`; otherwise it matches against it directly.
- **Locking:** `update`, `repo add` and `repo remove` take the mutation
  lock (D5). Read-only commands do not.
- Client verification order: root (walk `N+1…` sequentially when
  rotating) → timestamp → index → manifest digest → artifact SHA-256.
- **Replay and rollback protection:** store the highest accepted
  `version` for each role and repository in SQLite, and reject any
  lower version. Reject expired metadata after allowing
  `trust.maxClockSkewSeconds`.
- **Key rotation:** root `N+1` must be signed by a threshold of root `N`
  keys **and** a threshold of its own keys.
- **Bootstrap:** `repo add` downloads `root.json`, displays the root
  key fingerprints and requires approval. Non-interactive use needs
  `--trust-root <fingerprint>` (repeatable), and every fingerprint must
  match. No trust root is ever added implicitly.
- Repositories must keep **all historical manifests** published, so
  locked installs keep working.
- There is no built-in default public repository in v1. The project
  owner adds an official repository and its root keys once release
  infrastructure exists.

### D7. Mark-of-the-Web
WinBrew does not add `Zone.Identifier` to downloaded or extracted files.
Authenticity comes from D6 signatures and SHA-256 verification.
Record the consequence in `security-model.md` and `known-limitations.md`:
SmartScreen's reputation check does not apply to WinBrew-installed
binaries.

### D8. Uninstall and orphans
- `uninstall` refuses if another installed package depends on the
  target (exit 6) and names the dependents.
- Packages installed only as dependencies stay installed after their
  dependents are removed. `wbrew autoremove [--dry-run]` removes
  dependency-only packages that nothing references any more.

### D9. Exit codes

| Code | Name | Meaning |
|---|---|---|
| 0 | `Success` | Operation completed |
| 1 | `GeneralFailure` | Unexpected or unclassified failure |
| 2 | `UsageError` | Unknown command, invalid arguments, invalid input, ambiguous package ID |
| 3 | `NotFound` | Package, version, repository or config key not found |
| 4 | `NetworkFailure` | Download or metadata fetch failed after retries |
| 5 | `VerificationFailure` | Checksum, signature, expiry, rollback or archive-safety failure |
| 6 | `ConflictOrRefused` | Unresolvable dependencies, shim collision, dependent prevents uninstall, pinned |
| 7 | `LockUnavailable` | Another WinBrew process holds the mutation lock |
| 8 | `PartialOrRecoveryRequired` | Incomplete operation, failed recovery or ambiguous installer outcome |
| 9 | `RebootRequired` | Installer completed and a reboot is required or was started |
| 10 | `ConsentRequired` | A prompt would be needed but the session is non-interactive |
| 11 | `FilesInUse` | Files are locked by running processes (named in the output) |
| 12 | `InsufficientSpace` | Disk full or quota exceeded |
| 13 | `InstallerBusy` | Another MSI installation is in progress (msiexec `1618`) and retries were exhausted |
| 130 | `Cancelled` | Ctrl+C or cancellation |

- The mapping lives in one place (`WinBrew.Cli/ExitCodes.cs`) and
  `cli-reference.md` documents it.
- **Note on 130:** `130` is the POSIX cancel convention; Windows
  conventionally uses `0xC000013A`. WinBrew keeps `130` for CLI
  ergonomics and documents this deviation explicitly in
  `cli-reference.md`.

### D10. Configuration keys

| Key | Type | Default | Notes |
|---|---|---|---|
| `color` | `auto`/`always`/`never` | `auto` | `--no-color` and `NO_COLOR` override it |
| `interactive` | `auto`/`never` | `auto` | `never` behaves like `--non-interactive` |
| `cache.maxBytes` | int64 | 5368709120 | Cache budget for `cleanup` |
| `download.timeoutSeconds` | int | 300 | Per artifact |
| `download.maxBytes` | int64 | 4294967296 | Hard limit per artifact |
| `download.retries` | int (0–5) | 3 | Exponential backoff with jitter |
| `retainVersions` | int (0–5) | 1 | Inactive portable versions kept for recovery |
| `lock.timeoutSeconds` | int | 30 | |
| `trust.maxClockSkewSeconds` | int | 300 | |
| `trust.maxMetadataBytes` | int64 | 67108864 | Max size of any metadata file (e.g. `index.json`); enforced against `timestamp.json`'s `length` while streaming |
| `archive.maxEntries` | int | 100000 | |
| `archive.maxTotalBytes` | int64 | 8589934592 | |
| `archive.maxCompressionRatio` | int | 200 | Checked per entry and in total |
| `path.managed` | bool | false | Set to true only after the user consents |
| `testing.allowInsecureLocalhost` | bool | false | Honored only in test-hooks builds (`-p:WinBrewTestHooks=true`) with `WINBREW_TEST_MODE=1`. Release builds reject the key in `config set` with exit 2, and `doctor` reports it if found in `config.json` |

- `config set` validates the type and range. Unknown keys exit with 2.
  In release builds, `testing.*` keys are rejected with exit 2 as well
  (D10 table).
- `config.json` has a schema (`config.v1.schema.json`) and a
  `schemaVersion` field.
- Credentials are **never** stored in `config.json` (see A13).

### D11. Telemetry
None. WinBrew makes network requests only to configured repository
URLs, artifact URLs and the self-update feed. `security-model.md`
documents this.

## A8. Commands

### Global options
`--json`, `--no-color`, `--non-interactive`, `--yes`, `--verbose`,
`--quiet`, `--offline`

### Consent model
- `--yes` only approves the displayed **operation plan**.
- These require their own flags and are never implied by `--yes`:
  - `--trust-root <fingerprint>`: adding a trust root
  - `--modify-path`: changing the user PATH
  - `--allow-installer`: running an MSI/EXE (v1.1)
  - `--scope machine`: per-machine operations (v1.1)
- Non-interactive mode applies when `--non-interactive` is passed,
  `interactive=never` is set, `WINBREW_NONINTERACTIVE=1` is set, or
  stdin is not a terminal. In that mode, any missing consent exits with
  10 and nothing is prompted.

### Command list

```
wbrew --version
wbrew search <query> [--json]
wbrew info <package> [--json]
wbrew install <package>[@<version>] [--version <version>] [--dry-run]
wbrew uninstall <package> [--dry-run]
wbrew autoremove [--dry-run]
wbrew list [--json]
wbrew outdated [--json]
wbrew update                      # Refresh repository metadata only
wbrew upgrade [package] [--dry-run]   # Change installed versions
wbrew pin <package>
wbrew unpin <package>
wbrew cleanup [--dry-run]
wbrew doctor [--json]
wbrew recover [--dry-run]
wbrew repo add <name> <url> [--trust-root <fingerprint>...]
wbrew repo list [--json]
wbrew repo remove <name>
wbrew bundle export --file <path> [--lock <path>]
wbrew bundle install --file <path> [--lock <path>] [--locked]
wbrew config get <key>
wbrew config set <key> <value>
wbrew config list [--json]
wbrew self install [--modify-path]
wbrew self update [--dry-run]
wbrew self uninstall [--keep-data]
```

### Command-specific rules
- **`install`:** specifying both `<package>@<version>` and `--version`
  is a usage error (exit 2).
- **`--dry-run`:** prints the full plan computed from the **existing
  catalog**; it never runs `update` and never refreshes metadata. If
  the catalog is missing or expired, exit 3 or 5 with advice to run
  `wbrew update`.
- **Expired metadata online:** when the accepted `timestamp.json` has
  expired, online mutating commands that need metadata (`install`,
  `upgrade`, `bundle install`) refresh it automatically through the
  normal verified `update` path — taking the mutation lock — and say
  so in the plan output ("Repository metadata refreshed: timestamp
  expired 2 days ago"). Read-only commands (`search`, `info`,
  `outdated`, `list`) show results from expired-but-once-valid
  metadata with a warning and never refresh; if no metadata was ever
  verified for a configured repository, they exit 3 with advice to run
  `wbrew update`. `--offline` behavior is defined in A15 and never
  refreshes.

### Output rules
- With `--json`, stdout carries exactly one JSON document following
  `cli-output.v1.schema.json`:
  ```json
  { "schemaVersion": "1", "command": "list", "ok": true,
    "data": { }, "warnings": [ ] }
  ```
  On failure: `"ok": false, "error": { "code": 6, "name": "ConflictOrRefused", "message": "...", "details": { } }`.
- The CLI output schema is versioned separately from the manifest schema.
- Progress, spinners, color and decoration go only to an interactive
  terminal and are never written to stdout when `--json` is used or
  stdout is redirected.
- Diagnostics and logs go to stderr and to `logs\`.
- Before any mutation, `--dry-run` prints the full plan and exits 0
  without changing anything: no downloads to the cache, no state
  writes, no shims.

## A9. Package manifest

`schemas/manifest.v1.schema.json` plus typed C# models and a validator
that enforces rules beyond the JSON Schema.

Required content for each package release:
- `schemaVersion` (`"1"`)
- `id`: normalized lowercase, matching `^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$`,
  with no `..` and no reserved device names
- `version` (normalized, D3) and optional `vendorVersion`
- `displayName`, `description`, `homepage` (HTTPS), `license` (SPDX expression)
- `scope`: `["user"]` for portable packages. MSI/EXE packages may
  declare `user` and/or `machine` (v1.1).
- `writesToInstallDir`: optional bool, default `false`. Set `true` when
  the application writes configuration or state inside its installation
  directory (that state will not survive upgrades, A3). `wbrew info`
  shows a warning when it is set.
- `artifacts[]`, one per architecture (`x64`, `arm64`, or `x86`; the
  A10 selection order decides which is used on each host), each with:
  - `url` (HTTPS), `sha256` (64 lowercase hex), `size` (bytes)
  - `format`: `zip` | `msi` | `exe`
  - `installType`: `portable` | `msi` | `exe`
- `dependencies[]`: `{ "id": "<id or repo/id>", "range": "<interval>" }`
  (must exactly match the corresponding `index.json` version entry, D6).
  **Dependency ID qualification:** an unqualified dependency ID always
  refers to the repository of the package that declares it; it is never
  resolved against other repositories. A cross-repository dependency
  must be fully qualified (`<repo>/<id>`) and is resolved only if that
  repository is configured; otherwise resolution fails with exit 3,
  naming the missing repository.
- `executables[]` (portable only): `{ "name": "<shim name>", "target": "<relative path inside package>" }`.
  The target must be an `.exe`; each executable's PE machine type is
  validated against the host (A10).
- `installer` (MSI/EXE only, v1.1): see A12.

Package identity is `<repo>/<id>`. An unqualified ID that matches
packages in more than one repository is an error (exit 2) that lists
the qualified candidates. There is no implicit repository priority.
Repository names match `^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$`.

Installer declarations are **executable-code trust decisions**, not
safe data. The documentation must say so.

Example manifests point only to locally built fixture packages served
by the test HTTP server. Do not invent public URLs, checksums or vendor
installer switches.

## A10. Dependency resolution requirements

- Resolve the full graph before any change to the machine.
- Support transitive dependencies, ranges, pins, architecture filtering,
  cycle detection with a reported path, and conflict explanations.
- **Host architecture detection:** detect the host with
  `IsWow64Process2` (native machine) when available, falling back to
  `RuntimeInformation.OSArchitecture`. **Never use
  `RuntimeInformation.ProcessArchitecture`**: it reports the emulated
  architecture (x64) when an x64 `wbrew` build runs under emulation on
  ARM64, which would select the wrong artifacts.
- **Artifact selection order (explicit fallback):**

  | Host OS | Artifact `architecture` accepted, in order |
  |---|---|
  | x64 | `x64` → `x86` |
  | arm64 | `arm64` → `x64` → `x86` |

  An `x86` artifact is selected only when no native artifact exists for
  the host. The chosen fallback appears in the plan and as a warning in
  `--json` output. This fallback order must be stated in the ADR and in
  the plan.
- **Selection is re-run on every install and upgrade.** An upgrade may
  change the selected architecture (for example, v1 shipped only `x64`
  and v2 adds `arm64`). When it changes, the plan shows the transition
  (`x64 → arm64`) and the new architecture is recorded in state and in
  lockfiles.
- **PE machine-type validation** (applies to every declared executable,
  see A11 step 6):

  | Host OS | Accepted PE machine types |
  |---|---|
  | x64 | `0x8664` (AMD64), `0x014C` (I386 — includes IL-only/AnyCPU .NET Framework images) |
  | arm64 | `0xAA64` (ARM64, including ARM64X), `0x8664` (AMD64, including ARM64EC), `0x014C` (I386) |

  Reject `0x01C4` (ARM32 — unsupported by Windows 11 24H2 and later)
  and every unknown machine type.
- **Manifest-vs-index exact match:** after resolution, WinBrew fetches
  only the selected manifests, verifies their digests against
  `index.json`, and requires each manifest's `dependencies` and artifact
  `architectures` to **exactly match** the corresponding catalog entry
  (D2, D6). Any mismatch exits with 5 and makes no changes.
- Only one active version per package identity.
- Portable packages can only depend on portable packages.
- Store explicit-versus-dependency installs and every dependency edge.
- Cleanup never removes active, pinned, referenced or
  recovery-retained artifacts.

## A11. Portable package lifecycle

### Steps
1. Resolve metadata and dependencies (pure planning, D2).
2. Show the plan and get consent (D9 code 10 if consent is needed but
   the session is non-interactive).
3. Download to `staging\<op>\download.tmp`, enforcing `size`,
   `download.maxBytes` and the timeout, and hashing as the data streams.
4. Verify SHA-256 **before** extraction. Move verified artifacts into
   `cache\artifacts\<sha256>`.
5. Extract into `staging\<op>\extract\` with the archive defenses in A13.
6. Validate declared executables: they exist, they are regular files,
   and their PE headers have a machine type accepted for the host (see
   the A10 table — x86/`0x014C` is valid on both hosts, ARM32
   `0x01C4` and unknown types are always rejected). Also check for
   shim-name collisions.
7. Rename the directory into `packages\<id>\<version>\<arch>\`.
8. Activate shims and update installed state.
9. Mark the operation complete in the journal.

### State machine and journal
- Files and SQLite are separate resources. Never claim one atomic
  transaction covers both.
- Each operation has a row in `operations` and ordered rows in
  `operation_steps`, with intent written **before** and outcome written
  **after** each filesystem action.
- States: `Planned → Downloaded → Verified → Extracted → Validated →
  Placed → ShimsActivated → StateCommitted → Completed`, plus
  `Compensating → RolledBack` and `RecoveryFailed`.
- Every step and its compensating action must be **idempotent**.
- At startup, every mutating command (and `wbrew recover`) takes the
  lock, finds incomplete operations, and either rolls forward or
  compensates. Read-only commands report pending recovery as a warning.
- **Upgrade:** the previous version's directory and shims stay intact
  until `StateCommitted`. Compensation points the shims back at the
  previous version (updating `<name>.shim.json` per D4). If recovery
  cannot reach a consistent state, the operation is marked
  `RecoveryFailed`, exits with 8, and reports exactly what is
  inconsistent. It never claims success.

### Fault-injection seam
- `IFaultInjector.CheckpointAsync(string name, CancellationToken)` is
  called at every durable transition.
- The production implementation does nothing. A **test-hooks build**
  (`-p:WinBrewTestHooks=true`) reads `WINBREW_FAULT_AT=<name>` and
  terminates the process (`Environment.FailFast`) at that checkpoint.
  This one build property gates **all** test-only behavior: the
  fault-injection seam and the A13 localhost HTTP exception. Release
  builds contain neither.
- A registry of every checkpoint name is exposed to tests, so the test
  suite can enumerate them all.

## A12. Windows-specific behavior

### PATH
- Only `%LOCALAPPDATA%\WinBrew\bin` is ever added, and only to
  `HKCU\Environment\Path`, after consent (`--modify-path` or a prompt).
- If the `HKCU\Environment\Path` value does not exist, create it as
  `REG_EXPAND_SZ`. Refuse (exit 6) if the resulting value would exceed
  32,767 characters; warn when it exceeds 2,047.
- Read the raw value with `RegistryValueOptions.DoNotExpandEnvironmentNames`,
  keep the original value kind (`REG_EXPAND_SZ`/`REG_SZ`), keep the
  order of existing entries, compare entries case-insensitively with
  normalized trailing slashes, and never add a duplicate.
- Broadcast `WM_SETTINGCHANGE` with `"Environment"` through
  `SendMessageTimeout`.
- Tell the user that shells already running may need to be reopened.
- Removal deletes only the exact entry WinBrew added.

### Shims
See D4. Before activation, detect collisions with existing shims from
another package, unmanaged files in `bin\`, and same-named commands
that appear earlier on PATH (a warning, not an error). Never overwrite
an unmanaged file.

### Long paths
- Embed an application manifest with `longPathAware=true` in `wbrew`
  and the shim.
- The validator warns when a package's deepest installed path would
  exceed 260 characters. `doctor` reports the system `LongPathsEnabled`
  setting.

### Files in use and antivirus interference
- On sharing violations during move, delete or replace, retry with
  bounded backoff (5 attempts, about 3 seconds total).
- If files are still locked, use the **Restart Manager API**
  (`RmStartSession`, `RmRegisterResources`, `RmGetList`) to name the
  processes holding them, then exit with 11. Never terminate a process.

### MSI (v1.1, Phase E)
- Invoke `msiexec.exe` from `%SystemRoot%\System32` through
  `ProcessStartInfo.ArgumentList`. No shell is used and no string is
  concatenated into a command line.
- Install: `/i <path> /qn /norestart /l*v <log>` plus only the
  properties declared in the manifest.
- Uninstall: `/x <ProductCode> /qn /norestart /l*v <log>`.
- Exit code handling: `0` success; `3010` reboot required → 9;
  `1641` reboot started → 9; `1602` user cancelled → 130;
  `1618` another installation in progress → retry with backoff, then
  exit **13 `InstallerBusy`** (D9); anything else, such as `1603` → 1,
  with the log path included.
- Track `ProductCode`, `UpgradeCode`, scope and version.
- Per-user install only when the manifest declares `user` scope **and**
  the property set the package was authored for. After installing,
  confirm the actual context with `MsiGetProductInfoEx` and record it.
- Reconciliation is explicit: `list` and `doctor` query
  `MsiQueryProductState` and report drift (for example, the product was
  removed outside WinBrew). They never correct state silently.
- Per-machine installs are recorded in the user's database and
  reconciled through ProductCode. Other users' WinBrew instances will
  not see them. Document this as a known limitation.

### EXE installers (v1.1, Phase E)
- Allowed only when the manifest has `installer.type = "exe"` with an
  explicit `silentInstallArgs[]`, `silentUninstall` (an executable path
  pattern plus an argument array) and `detection` rules, and the user
  passes `--allow-installer`.
- Never guess switches. Never run registry `UninstallString` values
  through a shell. If uninstall metadata cannot be executed safely
  without a shell, refuse.
- Detection rules: registry uninstall key name and/or file
  existence/version. If detection is ambiguous after the installer runs,
  mark state `Unknown` and exit with 8.

### Elevated helper (v1.1, Phase E)
- `WinBrew.ElevatedHelper.exe` is a separate small executable started
  through `ShellExecute` with the `runas` verb, once per elevated
  operation. The rest of WinBrew is never elevated.
- It accepts only a typed request file (`msi-install`, `msi-uninstall`,
  `exe-install`, `exe-uninstall`) with strict schema validation. It
  never accepts a command line, script or arbitrary executable path.
- **Request transport:** the caller passes the request file's path as
  the helper's **sole argument** through `ShellExecute` (`runas`).
  The non-elevated WinBrew writes the request file under
  `%LOCALAPPDATA%\WinBrew\staging\elevated\`; it names the operation
  type, the artifact's source path and the expected SHA-256. The
  helper treats the request file as untrusted input (strict schema
  validation) and always copies the artifact into its protected
  `<op>` directory, verifying the held-handle hash against the
  request, before anything runs.
- **TOCTOU and directory pre-creation protection — the helper never
  adopts a directory it did not create.** Standard users can create
  folders under `%ProgramData%` by default, so the helper treats
  `%ProgramData%\WinBrew` and `%ProgramData%\WinBrew\elevated` as
  untrusted whenever they already exist. Adopting an existing
  directory (taking ownership of it or re-applying ACLs) is unsafe in
  three ways — open handles survive DACL changes, recursive re-ACLing
  follows hard links, and the attacker who owns the directory swaps it
  between a check and the change (RATIONALE.md, R6) — so it is never
  done. Concretely:
  1. Before trusting an existing `%ProgramData%\WinBrew` (or
     `%ProgramData%\WinBrew\elevated`): the owner must be
     `BUILTIN\Administrators` or `NT AUTHORITY\SYSTEM`, **and** no
     principal other than those two may hold write, append, delete,
     `WRITE_DAC` or `WRITE_OWNER` — granted explicitly or through
     inheritance. Check the actual DACL, not just the owner. Inspect
     each path component by opening it with
     `FILE_FLAG_OPEN_REPARSE_POINT` and checking the attributes on the
     opened handle, never by path. If any check fails, or any
     component is a reparse point, **refuse** with a diagnostic and
     remediation instructions. Do not repair it.
  2. Each directory the helper needs is created through
     `CreateDirectoryW` (P/Invoke) carrying its **final security
     descriptor**: a protected DACL
     (`SetAccessRuleProtection(isProtected: true, preserveInheritance: false)`)
     that grants write/modify access **only** to
     `BUILTIN\Administrators` and `NT AUTHORITY\SYSTEM`, with the owner
     set explicitly to `BUILTIN\Administrators`. Do not use
     `FileSystemAclExtensions.Create(DirectoryInfo, DirectorySecurity)`
     for this: it does nothing when the directory already exists,
     silently turning "created" into "adopted". On
     `ERROR_ALREADY_EXISTS`, run the step-1 checks on the existing
     directory. Never assume a directory is new.
  3. The `<op>` directory name is random and generated by the helper.
     It never comes from the request.
  4. Copy the artifact into `%ProgramData%\WinBrew\elevated\<op>\`,
     then open it with **`FileShare.Read` only** (no write or delete
     sharing), hash it **from that handle**, compare the hash with the
     request, and **keep the handle open** for as long as `msiexec` or
     the EXE runs. It never runs files from user-writable locations.
  5. ACLs are **never** applied recursively to content that already
     existed.
- Phase E must test: pre-creating `%ProgramData%\WinBrew` as a standard
  user — as a plain directory, as a junction, and containing a hard
  link — must cause refusal in every case, with no ACL changed outside
  directories the helper created.
- Document the remaining risk: malware already running as the same user
  can influence what that user approves at a UAC prompt.
- The helper writes a typed result (exit code, log path, detected
  ProductCode) and deletes its working `<op>` directory.
- Rollback is **not** promised for MSI/EXE operations.

## A13. Security and trust

- HTTPS only for repositories and downloads. Exception:
  `http://localhost`, `http://127.0.0.1` and `http://[::1]`, and only
  when **all** of the following hold:
  - the build was compiled with `-p:WinBrewTestHooks=true` (the
    localhost exception is **compiled out** of release builds);
  - `testing.allowInsecureLocalhost=true`; and
  - `WINBREW_TEST_MODE=1`.
  In a release build, neither the config flag nor the environment
  variable has any effect, and `doctor` reports both when it finds
  them set. E2E never uses this exception: it serves fixtures over
  HTTPS with a per-job test CA (A16).
- Redirects are followed manually, at most 5. Reject any redirect to a
  non-HTTPS URL. Remove `Authorization` and any credential when the
  origin (scheme + host + port) changes.
- Credentials come from Windows Credential Manager (generic credential
  `WinBrew:<origin>`) and are sent only to that exact origin.
- Logging redacts `Authorization` headers, cookies, URL userinfo, and
  every URL query string (signed URLs carry secrets there).
- Validate repository names, manifests, relative paths and every
  archive entry.
- **Archive defenses** (ZIP). Reject:
  - absolute paths, drive letters, UNC paths, and `..` segments
  - backslash/forward-slash ambiguity (normalize, then re-check)
  - reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM0–9`,
    `LPT0–9`, `CONIN$`, `CONOUT$`, with or without extensions), in any
    path segment
  - `:` in names (alternate data streams), trailing dots or spaces
  - case-insensitive duplicate paths
  - entries with Unix symlink mode bits
  - more than `archive.maxEntries` entries, extracted totals above
    `archive.maxTotalBytes`, and per-entry or total compression ratios
    above `archive.maxCompressionRatio`. Enforce limits on **actual**
    decompressed bytes during streaming, not on the declared sizes in
    the header.
- After extraction, check that each final path is inside the staging
  root (full path normalization with a trailing separator), and walk
  the tree to confirm there are no reparse points
  (`FileAttributes.ReparsePoint`).
- Cancellation, disk exhaustion (exit 12) and network failure are
  recoverable. Staging is cleaned up and state stays consistent.
- Checksums verify integrity against metadata. Signatures (D6)
  authenticate metadata. Both are required.
- Negative tests must cover tampered, expired, rolled-back, replayed,
  wrong-key (and wrong-curve, and explicit-parameters), below-threshold,
  duplicate-key-ID, wrong-signature-length and freeze-attack (stale
  timestamp) metadata.

## A14. Bootstrap, self-update and self-uninstall

- **First install:** the user downloads `wbrew-<ver>-win-<arch>.zip`
  from the release page. The zip contains the files recorded by the
  Phase A publish-output test — at minimum `wbrew.exe` (managed
  single-file bundle), `e_sqlite3.dll` and `wbrew-shim.exe` (D4). The
  user extracts it anywhere and runs `wbrew self install
  [--modify-path]`, which copies all of them to `app\<ver>\`, then
  creates `bin\wbrew.exe` as a copy of `app\<ver>\wbrew-shim.exe` with
  a sidecar pointing to `app\<ver>\wbrew.exe`, initializes state and
  offers the PATH change. Nothing is ever extracted to `%TEMP%`;
  native libraries load from `app\<ver>\`.
- **Self-update:** `wbrew self update` reads a signed release feed
  using the **same envelope, roles and key model as D6**. The release
  root key compiled into the binary is only the **initial** root
  (N=1); the feed's root chain rotates exactly like D6 (root `N+1`
  signed by a threshold of root `N` keys **and** a threshold of its own
  keys), so keys can be rotated without shipping a new binary.
  `self update` downloads and verifies the new version, places it in
  `app\<newver>\` including the new `wbrew-shim.exe` template, then
  switches the `wbrew` shim sidecar atomically and replaces every shim
  copy as one journaled bulk operation (D4). The running executable is
  never overwritten. The previous version is kept until the next
  successful start.
- If no release feed URL is configured in the build, `self update`
  exits with 3 and the message "self-update not configured for this build".
- **Self-uninstall:** removes the PATH entry and shims, and (unless
  `--keep-data` is passed) packages, cache and state. It refuses if any
  v1.1 installer-managed packages are still installed. Because the
  running executable's directory cannot be deleted, WinBrew reports
  that path for manual removal. It does not use shell tricks.

## A15. Bundles and reproducibility

- **Bundle** (`bundle.v1.schema.json`): requested packages with
  optional ranges and pins, plus repository names and URLs.
- **Lockfile** (`lockfile.v1.schema.json`): for each package, the
  exact version, architecture, repository identity (name, URL, root key
  fingerprints), manifest SHA-256, artifact URL and artifact SHA-256,
  and explicit or dependency status.
- `bundle install --locked` never re-resolves. The lockfile path comes
  from `--lock <path>`, defaulting to `<bundle>.lock.json` beside the
  bundle file. It fetches the exact
  manifests, checks their digests against the lockfile and fails
  clearly (exit 3 or 5) if any entry is missing or different.
- In `--offline` mode, locked installs succeed only from verified cache
  entries. Expired metadata may be used offline only when it was
  **fully verified while still valid** — metadata that was never valid
  is never accepted, online or offline. Offline use of expired metadata
  requires `--offline` and shows a warning that includes the
  metadata's age. Online installs never accept expired metadata:
  they refresh it first, automatically (A8).
- The documentation must not claim that external installers or mutable
  upstream resources are reproducible.

## A16. Testing requirements

**Unit:** manifest validation (including `writesToInstallDir`),
version and range parsing (including the bare-version rejection),
PubGrub conflicts and explanations (repository-qualified IDs and
original range strings), "active constraints" and ordinal tie-breaking,
cycles, pins, architecture filtering (artifact selection order, A10),
plan generation, path and archive defenses, shim collisions, config
validation, exit-code mapping (including 13), JSON output schema
conformance, PATH string manipulation (including the missing-value and
length-limit rules), redirect and credential policy, SPKI parsing and
signature verification (algorithm OID, named-curve parameters,
uncompressed point, 64-byte signatures, duplicate key IDs, threshold
counting) and every trust negative case.

**Integration** (runs against the **test-hooks build**
`-p:WinBrewTestHooks=true` where fault injection is needed): a local
HTTP test server (Kestrel, in-process), ZIP fixtures built locally at
test time, isolated `WINBREW_ROOT` per test, interrupted and truncated
downloads, corrupt artifacts, malicious archives, migrations from every
prior schema version, backups, **real child processes** for concurrency
and lock tests (including verifying that a blocked process reports the
holder PID from `winbrew.lock`, that torn/partial lock metadata is
treated as "holder unknown", and that a reused PID with a mismatched
start time is not reported as the holder), locked files (opened with
`FileShare.None`), upgrading a package while its shim is actively
running, read-only and ACL-denied directories, disk-full simulation (a
quota-limited VHD if available, otherwise a fault-injected
`IOException` with `HResult` 0x80070070), and a crash at **every**
registered fault checkpoint followed by `wbrew recover`.
**Crash/fault-injection tests live here, never in E2E.**

**End-to-end** (clean Windows runner, published **release** binary):
self install, PATH change in a fresh process, install, execute through
the shim (exit codes, quoting corpus, stdin/stdout piping, Ctrl+C
forwarded to the child), upgrade, failed upgrade with recovery,
uninstall, autoremove, bundle export plus locked restore, offline
restore from cache, and native execution on matching x64 and ARM64
runners.
- **HTTPS only:** the E2E job generates a throwaway root CA and a
  `localhost` leaf certificate (SAN: `localhost`, `127.0.0.1`, `::1`)
  at job start; nothing is committed to the repository. The CA is
  installed into `LocalMachine\Root` on the disposable runner — never
  `CurrentUser\Root`, where adding a root shows a confirmation dialog
  that blocks non-interactive runs. Kestrel serves the fixture
  repository and artifacts with the leaf certificate, and the CA is
  removed in an `always()` cleanup step. Installing the test CA on a
  developer machine requires the explicit opt-in switch
  (`scripts/test.ps1 -E2E -InstallTestCA`). A sanity test asserts the
  release binary refuses `http://localhost` even with
  `WINBREW_TEST_MODE=1` and `testing.allowInsecureLocalhost=true` set.
- **Deterministic interruption:** the fixture server is throttled and
  killed after a fixed number of download bytes (or when a marker file
  appears); the test then runs `wbrew recover` and asserts the
  previous version still works. E2E covers recovery only through this
  real interruption — it uses no fault-injection build.
- From the extracted release zip, `self install` creates
  `bin\wbrew.exe` and `wbrew --version` works from a fresh shell.
- E2E also asserts that the published binaries never create an
  extraction directory under `%TEMP%` on a fresh profile.

**Installers (v1.1):** run MSI and EXE lifecycle tests only on
disposable Windows VMs or runners, with fixture installers built in CI
(for example a WiX-built test MSI). Mocked tests support these real
tests but do not replace them. Include the elevated-helper refusal
tests (A12): pre-created `%ProgramData%\WinBrew` as a plain directory,
as a junction, and with a hard link inside must be refused in every
case.

**Argument quoting corpus:** a table-driven test of at least **35**
cases: empty arguments, spaces, embedded quotes, trailing backslashes,
backslashes before quotes, Unicode, `%`/`^`/`&`/`|`, and very long
command lines, tested with both quoted and unquoted `argv[0]` shim
invocations. Expected forwarded tails include: `C:\Pro"gram
Fi"les\x.exe a` → ` a`; `"x.exe"arg` → empty (no arguments are
forwarded); `"C:\a b\x.exe" "q r" s` → ` "q r" s` (unchanged); a tab
separator (`x.exe<TAB>-v`) → `<TAB>-v`; an unterminated quote
(`"x.exe`) → empty; an empty command line → empty; and a case that
overflows the 32,767-character command-line limit (asserting exit
206). The corpus must also assert that a backslash inside `argv[0]`
has no special meaning.

**If ARM64 runners are not available,** the ARM64 E2E job reports
**BLOCKED** in the readiness report. It is never skipped silently or
counted as passing.

## A17. Non-functional targets

Measure on the CI runners (post-`self install` — i.e. **warm start**)
and report the measured numbers. Never invent them. The search target
is measured on the CI runner against a **generated** 10,000-package
catalog, reporting the **median of 10 runs**.

| Metric | Target |
|---|---|
| `wbrew --version` warm start (post-`self install`, fast-path) | < 300 ms |
| `wbrew --version` first run (fresh user profile) | Report only; no target |
| `wbrew list` with 200 packages installed | < 500 ms |
| `wbrew search` on a generated 10,000-package catalog (SQLite), median of 10 runs, CI runner | < 1 s |
| Shim overhead per invocation | < 30 ms |
| Shim binary size | < 3 MB |
| `wbrew.exe` bundle size and shipped zip size | Report it; no hard limit |

Also verify by test (never assume) that the published binaries create
no extraction directory under `%TEMP%`, and record the observed
behavior in the ADR.

## A18. Documentation and release

**Documents:** README with quick start, architecture document, ADRs
(one per D-decision, plus: the MSVC C-runtime `argv[0]` parser rules,
the publish-output manifest and the observed publish behavior, the x86
artifact fallback policy, the sidecar containment exception, and the
exit-130 convention), CLI reference including exit codes (13 and 130
included)
and the JSON schema, manifest specification (including
`writesToInstallDir`), repository and trust specification, package
author guide — including the `persist` exclusion and its consequences
and the `writesToInstallDir` field — security model, threat model
(STRIDE-style table), recovery and troubleshooting guide, known
limitations, contribution guide, license, changelog, release checklist,
versioned example bundle and lockfile, and PowerShell completion
(`scripts/wbrew-completion.ps1`, built on System.CommandLine's
completion support).

**CI (`ci.yml`):** restore in locked mode **for every RID it publishes**
(per-RID `packages.lock.json`, A4), build, analyze, unit, integration
and E2E tests on x64 (`windows-latest`) and ARM64 (the current
GitHub-hosted Windows ARM64 runner label, such as `windows-11-arm`),
and publish both architectures as build artifacts.

**Release (`release.yml`):** triggered by a tag, publishes
`wbrew-<ver>-win-<arch>.zip` for `win-x64` and `win-arm64` (each
containing the Phase A publish-output set — at minimum `wbrew.exe`,
`e_sqlite3.dll` and `wbrew-shim.exe`), produces `SHA256SUMS.txt`
covering **every shipped file** (including `e_sqlite3.dll`,
`wbrew-shim.exe` and the zips), generates an SBOM (CycloneDX with a
pinned tool version), and signs `wbrew.exe`, `wbrew-shim.exe` and the
v1.1 helper with Authenticode from a **protected GitHub environment**.
A CI test fails if the zip contents differ from
`scripts/publish-files.txt`, and the signature verification covers
`wbrew-shim.exe`.

- **Signing route:** Microsoft's managed code-signing service
  (Trusted Signing — verify its current name, "Artifact Signing" as of
  late 2026, and its identity-validation/regional eligibility rules)
  if the project owner qualifies; otherwise an OV or EV certificate
  from a public CA (identity validation also required). If neither is
  available, artifacts are published clearly labeled **UNSIGNED**,
  listed as a known limitation, and flagged as a release blocker in
  the readiness report (C2).
- Never commit signing credentials or private keys. Test keys in
  `examples/repository/` are labeled **TEST ONLY**.
- If signing secrets are missing, the release job fails, or it
  publishes artifacts clearly labeled **UNSIGNED**. It never claims they
  are signed.
- `release-checklist.md` lists every step that needs external
  infrastructure: the code-signing route (managed service if the owner
  qualifies, otherwise a certificate) **and its owner**, ARM64
  runners, production repository hosting, production root keys
  (generated and stored offline), and the self-update feed URL.

## A19. Final acceptance criteria (v1.0 release candidate)

1. A clean, supported Windows 11 machine (x64 and ARM64) can run the
   self-contained CLI from the published zip without installing
   anything first and without anything being extracted to `%TEMP%`.
2. A standard user can self-install, and can install and remove
   portable packages without elevation.
3. Installed commands run correctly through shims (arguments, working
   directory, exit codes, Ctrl+C), including packages whose executables
   are x86 (`0x014C`) or AnyCPU PE images.
4. Dependency conflicts fail with explanations before any change to
   the machine.
5. Corrupt downloads, malicious archives and tampered, expired or
   replayed metadata are rejected.
6. A crash at any registered checkpoint recovers to a consistent state
   or reports `RecoveryFailed` honestly.
7. Upgrade (including while a shimmed command is running),
   failed-upgrade recovery, uninstall and autoremove are tested and
   documented.
8. Locked bundles reproduce exact selections or fail explicitly.
9. JSON output and exit codes match their specifications, verified by
   schema tests.
10. CI passes on the declared support matrix, or blockers are listed
    explicitly.
11. No critical execution path contains placeholders or fake success.
12. Known limitations and external release prerequisites are disclosed.

v1.1 adds: MSI and EXE lifecycles tested on disposable machines, the
elevation helper's refusal behavior tested (pre-created directories:
plain, junction, hard link), and reconciliation documented.
---

# PART C — TEMPLATES

## C1. Checkpoint report (end of every phase and sub-session)

```markdown
# Checkpoint: Phase <X> — <date>

## Environment
- OS / arch:
- .NET SDK (exact):
- Could execute builds/tests: yes | no (reason)

## Sub-session and phase status
- Sub-session: full | src | tests
- Phase status: COMPLETE | INCOMPLETE (remaining work: ...)
  # A phase is COMPLETE only after its test sub-session passes.

## Decisions applied / assumptions made
- ...

## Files changed
| Path | Change | Notes |
|---|---|---|

## Build and test results (actual output only)
| Command | Result | Evidence (summary / counts) |
|---|---|---|
| dotnet build -c Release | PASS / FAIL / NOT RUN | |
| dotnet restore --locked-mode -r win-x64 | | |
| dotnet restore --locked-mode -r win-arm64 | | |
| dotnet test tests/WinBrew.UnitTests | | |

## Fault checkpoints covered
| Checkpoint | Test | Result |
|---|---|---|

## Known gaps and deferred items
- ...

## Concerns about locked decisions
- ...

## Next session must start with
- Exact next steps, the files to open, and unresolved errors
```

## C2. Release-readiness report (end of Phase F)

```markdown
# Release Readiness — WinBrew <version>

| # | Criterion (A19) | Implementation | Tests | Evidence | Status | Blocker |
|---|---|---|---|---|---|---|
| 1 | Clean machine runs CLI (no %TEMP% extraction) | ... | ... | CI run link / log | PASS / FAIL / BLOCKED | |

## External prerequisites
| Item | Needed for | Status |
|---|---|---|
| Code-signing route: managed service (Trusted Signing / "Artifact Signing") if the owner qualifies, otherwise OV/EV certificate, + owner | Authenticode | |
| Windows ARM64 runners | ARM64 E2E | |
| Production repository hosting | Public use | |
| Production root keys (offline) | Trust bootstrap | |
| Self-update feed URL + release root chain | self update | |

## Measured non-functional results (A17)
| Metric | x64 | ARM64 | Target |
|---|---|---|---|
| `--version` warm start | | | < 300 ms |
| `--version` first run (fresh profile) | | | report only |
| `list` (200 packages) | | | < 500 ms |
| `search` (10k generated catalog, median of 10) | | | < 1 s |
| Shim overhead / size | | | < 30 ms / < 3 MB |
| Extraction assertion (no %TEMP%\.net\) | | | must pass |

## Known limitations
- ...
```

---

*End of PROMPT.md. Phases run in order; one phase per session; every
session starts with `PROMPT.md` plus exactly one phase file.*
