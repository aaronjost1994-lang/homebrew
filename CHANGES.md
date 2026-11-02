# WinBrew Implementation Prompt — Changelog and Review Traceability

**Document version:** 2.3.1 (2026-11-03)
**Companion files:** `PROMPT.md` (Part A + Part C), `phases/phase-0.md`
… `phases/phase-f.md` (Part B), `RATIONALE.md` (decision rationale).

> **This file is reference material for the project owner.** It is
> never sent to implementation sessions (review 4.6): it carries the
> changelogs, the correction of the disputed v2.1 claims, and the
> traceability appendices for both review rounds.

## Version history

| Version | Date | Input | Result |
|---|---|---|---|
| 2.0 | 2026-11-01 | — | First consolidated prompt (superseded) |
| 2.1 | 2026-11-02 | v2.0 review (chat) | 30 fixes claimed; several disputed (see the correction below) |
| 2.2 | 2026-11-03 | "Review: … v2.1" (30 findings) | Full rewrite; all 30 findings addressed (Appendix A) |
| 2.3 | 2026-11-03 | "Review: … v2.2" (18 findings) | All 18 findings verified against the v2.2 text and fixed; prompt split into session files (Appendix B) |
| 2.3.1 | 2026-11-03 | Consistency review of the file set | 9 planning-consistency fixes (below); phase files moved to `phases/`; README added |

## Changes in v2.3.1 (from v2.3)

A consistency review of the finished file set (not a new external
review). No design changes; every fix removes an ambiguity or an
unassigned item.

1. **Phase order vs D1 clarified.** Phases run 0, A–D, E, F; D1 now
   states that the Phase F release covers the v1.0 criteria (A19
   1–12) and lists the v1.1 additions, and that a portable-only v1.0
   build can be cut by tagging after Phase D. Rationale added
   (RATIONALE.md R17). (D1, Phase F)
2. **`self uninstall` assigned to Phase D** (completing the `self`
   lifecycle alongside `self update`); its installer-managed refusal
   is completed in Phase E. (A14; Phases D, E)
3. **Catalog `info` assigned to Phase D**, including A9's
   `writesToInstallDir` warning. (A9; Phase D)
4. **Phase files moved to `phases/`**, matching the routing table in
   `PROMPT.md` and the CHANGES/RATIONALE references; README added.
5. **R15's default count corrected** — eight defaults plus the
   System.CommandLine probe added in fix 9 (the old text said "five
   and five"). (RATIONALE.md)
6. **`scripts/publish-files.txt` added to the A5 tree.** (A4, A18)
7. **Bundle lockfile syntax defined:** `bundle install
   --lock <path>` with `--locked` as a mode flag; default lock path
   `<bundle>.lock.json`. (A8, A15)
8. **Elevated-helper request transport specified:** request path as
   the helper's sole argument, request file under
   `%LOCALAPPDATA%\WinBrew\staging\elevated\`, treated as untrusted.
   (A12)
9. **Phase 0 additions:** default decision 9 — probe for the exact
   stable System.CommandLine 2.0.x version; deliverable —
   per-project target frameworks. (Phase 0, A4)

## Changes in v2.3 (from v2.2)

Source: "Review: WinBrew .NET Implementation Prompt v2.2"
(2026-11-02). **All 18 findings were verified against the v2.2 text
before fixing — every one was confirmed** (Appendix B), including the
reviewer's own admitted omission (minor 16) and the two critical
issues (3.2, 3.4) that grew out of the reviewer's own v2.1
recommendations (items 4.2 and 4.1). The v2.1-fix verification table
in the review was also re-checked and is accurate: the new problems
are interactions *between* correct fixes, not regressions.

### Critical fixes

- **The shim template is now shipped and signed (3.1).** The release
  zip contains the Phase A publish-output set — at minimum
  `wbrew.exe`, `e_sqlite3.dll` and `wbrew-shim.exe` (the Native AOT
  build of `WinBrew.Shim` for the same RID). `self install` and
  `self update` copy all of them into `app\<ver>\`; the D4 template
  is `app\<active-ver>\wbrew-shim.exe`; `doctor` checks the active
  template against the hash recorded at install time; Authenticode
  signing and `SHA256SUMS.txt` cover `wbrew-shim.exe`. (A4, A6, A14,
  A16, A18, D4; Phases A, B, F)
- **The sidecar containment rule no longer blocks WinBrew's own shim
  (3.2).** D4 now allows — only for the sidecar named
  `wbrew.shim.json` — a target inside `<root>\app\`; every other
  sidecar must still target `packages\`. `<root>` is always the
  parent of the shim's own directory; the shim never reads
  `WINBREW_ROOT`. Containment normalizes with a trailing separator,
  refuses reparse points along the path, and is documented as defense
  in depth. (D4; Phase B)
- **E2E runs over HTTPS with a per-job test CA (3.3).** The release
  binary keeps the plain-HTTP localhost exception compiled out, so the
  E2E fixture repository is served by Kestrel over TLS with a
  throwaway CA installed into `LocalMachine\Root` on the disposable
  runner and removed in an `always()` step; developer-machine installs
  require `scripts/test.ps1 -E2E -InstallTestCA`. A sanity test
  asserts the release binary refuses `http://localhost`. (A13, A16;
  Phase 0)
- **The `argv[0]` rule no longer contradicts itself (3.4).** D4 now
  specifies the MSVC C-runtime quote-toggle scan verbatim (backslashes
  literal), deletes the separator-insertion rule (dead code under
  CRT semantics), and the A16 corpus asserts the real CRT outcomes:
  `"x.exe"arg` forwards nothing; the embedded-quote case forwards
  ` a`. (D4, A16; Phase B)

### Important fixes

- **Dependency IDs are deterministic (4.1).** An unqualified dependency
  ID always refers to the declaring package's repository; cross-repo
  dependencies must be fully qualified and resolve only if that
  repository is configured (else exit 3, naming the repository). (A9,
  D2; Phase C)
- **Phase B has a catalog (4.2).** Phase B ingests an unsigned
  `index.json` into the final catalog tables through the final ingest
  code, with signature verification behind `IMetadataVerifier`;
  `TestHooksUnsignedVerifier` is compiled out of release builds.
  Phase D swaps in the real verifier; a release build refuses an
  unsigned or partially signed index (exit 5), and a reflection test
  asserts the seam class is absent. (Phases B, D)
- **The helper's directory checks are closed (4.3).** Existing
  directories must pass a DACL check (no principal other than
  Administrators/SYSTEM with write, append, delete, `WRITE_DAC` or
  `WRITE_OWNER`, explicit or inherited); creation goes through
  `CreateDirectoryW` with the final `SECURITY_ATTRIBUTES` and explicit
  owner, handling `ERROR_ALREADY_EXISTS` by re-running the checks;
  parent components are inspected on opened handles
  (`FILE_FLAG_OPEN_REPARSE_POINT`). (A12; Phase E)
- **Expired-metadata behavior is defined (4.4, option a).** Online
  mutating commands auto-refresh through the verified `update` path,
  taking the lock and saying so in the plan; read-only commands serve
  expired-but-once-valid metadata with a warning and never refresh. (A8,
  A15; Phase 0)
- **The zip contents are verified, not assumed (4.5).** Phase A records
  the exact publish output per RID in the ADR and the committed
  `scripts/publish-files.txt`; the zip is that set minus `.pdb` files
  plus `wbrew-shim.exe`; a CI test fails on drift. (A4; Phases A, F)
- **The prompt is split into session-sized files (4.6).** `PROMPT.md`
  (Part A + Part C) is the only always-sent file; each phase prompt
  ships alone; the changelogs and traceability moved into this file;
  the decision rationale moved to `RATIONALE.md`; inline rationale was
  trimmed from the rules.
- **The signing route depends on eligibility (4.7).** Default:
  Microsoft's managed signing service (Trusted Signing — repackaged as
  "Artifact Signing" as of late 2026; verify current name and
  eligibility in Phase 0) if the owner qualifies; otherwise an OV/EV
  certificate; otherwise artifacts are labeled UNSIGNED, listed as a
  known limitation and a release blocker in C2. (A18, C2; Phase 0)

### Minor fixes

- **12:** A9's host-dependent "x86 only when no native artifact"
  sentence removed — the A10 order governs selection.
- **13:** artifact selection re-runs on every upgrade; the plan shows
  `x64 → arm64` transitions; state and lockfiles record the new
  architecture. (A10; Phase C)
- **14:** search uses a `search_text` column built with
  `ToLowerInvariant()` at ingest; queries lowercase the same way —
  case folding is never delegated to ASCII-only `LIKE`. (D6; Phase D)
- **15:** release builds reject `testing.allowInsecureLocalhost` in
  `config set` with exit 2; `doctor` reports it if found. (D10)
- **16:** replacing every shim after `self update` is one journaled
  bulk operation with one fault checkpoint per shim, idempotent and
  resumable by `recover`. (D4, A14)
- **17:** E2E interruption is deterministic — throttled fixture
  server, kill after a fixed byte count or a marker file, then
  `recover` and assert the previous version still works. (A16)
- **18:** the Phase 0 "already-decided inputs" are now **default
  decisions**: Phase 0 records each as adopted and must report any
  evidence that contradicts one. (Phase 0)

### The reviewer's five owner questions (§7) — defaults adopted

Recorded as Phase 0 default decisions, subject to contradicting
evidence per minor 18:

1. **Expired metadata** → auto-refresh (4.4a).
2. **Cross-repository dependencies** → allowed, fully qualified only
   (the §4.1 fix text); forbidding them entirely in v1.0 remains an
   option Phase 0 may recommend with reasons.
3. **Signing** → eligibility-conditional route (4.7). The service is
   now named **Artifact Signing** (formerly Trusted Signing) and
   identity validation is a real, sometimes slow eligibility gate.
4. **Test CA on CI** → accepted on disposable runners
   (`LocalMachine\Root`, `always()` cleanup); developer installs are
   opt-in only.
5. **File split** → adopted; this file set is the result.

---

## History

### Correction to the v2.1 changelog

The v2.1 changelog claimed fixes for cold start/extraction (A4/A14), PE
machine-type harmonization (A10/A11) and `%ProgramData%` hardening (A12)
that the v2.1 review disputed: the fast path cannot prevent native
extraction, `DOTNET_BUNDLE_EXTRACT_BASE_DIR` had no defined setter, the
PE rule rejected x86/AnyCPU executables, and the re-ACLing fix was
exploitable. v2.2 replaces those approaches with the ones below. The
other v2.1 changes (non-goals, lockfile honesty, System.CommandLine
anchor, PubGrub ordering, shim tail rules, lock holder metadata, catalog
indexing, sub-sessions) were judged sound in direction and are kept here,
corrected and completed.
### Changes in v2.2 (from v2.1)

**Critical fixes**

- **No native self-extraction (review 3.1, 3.2).** `wbrew` is published
  as a single-file *managed* bundle with
  `IncludeNativeLibrariesForSelfExtract=false`; `e_sqlite3.dll` ships
  next to the executable and both files are distributed as
  `wbrew-<ver>-win-<arch>.zip`. `self install` copies both into
  `app\<ver>\`. The `app\<ver>\extract\` directory, every
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` reference and the "(or pre-warms…)"
  alternative are removed. The .NET host extracts bundled native
  libraries before `Main` runs, so code in `Main` can never prevent
  extraction — v2.2 stops bundling them instead. A test asserts that no
  extraction directory appears under `%TEMP%`.
- **Sparse-index catalog (3.3).** Each `index.json` version entry now
  carries `sha256`, `size`, `architectures[]` and `dependencies[]`;
  the PubGrub resolver reads **only** the SQLite catalog snapshot; after
  resolution the selected manifests are fetched and required to **exactly
  match** the index entries (mismatch exits 5, no changes). `latestVersion`
  is removed; catalog rows and accepted role versions are written in one
  SQLite transaction; `update`, `repo add` and `repo remove` take the
  mutation lock; `trust.maxMetadataBytes` (64 MiB) is enforced from
  `timestamp.json`'s `length` while streaming; search semantics are
  defined (FTS5 optional, probed in Phase A).
- **PE machine types and host detection (3.4).** x86 (`0x014C`, including
  IL-only/AnyCPU images) is accepted on both hosts; the ARM64 host also
  accepts AMD64 (ARM64EC) and ARM64X; ARM32 (`0x01C4`) and unknown types
  are rejected. The host is detected with `IsWow64Process2` /
  `RuntimeInformation.OSArchitecture`, never `ProcessArchitecture` (which
  lies under emulation). `x86` becomes an allowed artifact architecture
  with an explicit selection order, and `doctor` warns when the `wbrew`
  build architecture does not match the OS architecture.
- **The elevated helper never adopts an existing directory (3.5).**
  Taking ownership and re-applying DACLs (v2.1) was exploitable: open
  handles survive DACL changes, recursive re-ACLing can follow hard
  links to system files, and the attacker owns the directory between
  checks. v2.2 refuses untrusted or reparse-point directories with
  remediation instructions, creates each directory atomically with its
  final protected security descriptor, generates the `<op>` name itself,
  and hashes the artifact from a `FileShare.Read`-only handle that stays
  open while the installer runs.
- **Lazy DI and a corrected System.CommandLine anchor (3.1, 4.7).** The
  anchor now builds services through a `Lazy<IServiceProvider>` on first
  use; parsing builds nothing. The action's return value is the process
  exit code, exceptions are mapped centrally in `ExitCodes.cs`, and
  System.CommandLine's default exception handling is disabled so the D9
  mapping and JSON error envelope stay authoritative.

**Important fixes**

- **Shim `argv[0]` tail (4.1):** one space inserted when the tail does
  not start with whitespace; parser choice fixed to MSVC C-runtime
  semantics and recorded in an ADR; command line checked against the
  32,767-character limit (overflow → one stderr line, exit 206); empty
  command line treated as an empty tail. Quoting corpus extended.
- **Shim console behavior (6.1, 6.2):** the shim installs a handler
  routine that returns `TRUE` and never calls
  `SetConsoleCtrlHandler(NULL, TRUE)` (an inheritable ignore flag);
  the job object allows `JOB_OBJECT_LIMIT_BREAKAWAY_OK` and continues
  without a job (logged) if assignment fails.
- **Live upgrades and sidecars (4.2):** the shim opens sidecars with
  `FileShare.Read | FileShare.Delete` and closes them before
  `CreateProcessW`; shim ownership comes from the database record, not
  the hash; `.old.<guid>` files are journaled and cleaned only by record,
  never by pattern; sidecar `target` must resolve inside `packages\`.
- **Lock metadata (4.3):** `processStartTime` recorded and checked
  (PID-reuse protection); torn reads treated as "holder unknown" and
  never as errors; no stale-lock breaking (the OS releases the lock when
  the holder dies); wait polls with backoff instead of busy-looping.
- **Signature validation (4.4):** SPKI parsed with `System.Formats.Asn1`
  before import (algorithm OID `1.2.840.10045.2.1`, parameters exactly
  the named-curve OID, uncompressed 65-byte point); `KeySize == 256`
  asserted after import; signatures must be exactly 64 bytes (P1363);
  duplicate key IDs rejected and each key counts once toward the
  threshold.
- **Resolver determinism (4.5):** "active constraints" defined; package
  choice (ordinal `<repo>/<id>`) separated from version choice (rules
  1–3); determinism required, performance tuning not; explanations name
  repository-qualified IDs and original range strings.
- **Per-RID lock files (4.6):** `RuntimeIdentifiers` added to
  `WinBrew.Cli` and `WinBrew.Shim` (helper in v1.1) so per-RID
  `packages.lock.json` files exist and CI can restore `--locked-mode`
  for every published RID.

**Minor fixes**

- `persist` exclusion surfaced to authors: package-author guide section
  plus optional `writesToInstallDir` manifest field shown as a warning
  by `info` (5.1).
- Phases A and B split into `src`/`tests` sub-sessions **by default**; a
  phase is complete only after its test sub-session passes; mid-phase
  checkpoints are marked `INCOMPLETE` (5.2, 5.3).
- A17 metric renamed **warm start**; a separate **first-run** metric is
  reported with no target (5.4); the search target is measured on the CI
  runner against a generated 10,000-package catalog, median of 10 runs
  (5.5).
- This changelog corrects the v2.1 claims disputed by the review (5.6).

**Carried over from v2.0, now fixed**

- Exit code **13 `InstallerBusy`** for msiexec `1618` after retries (6.3).
- Test-only behavior (fault-injection seam **and** the localhost HTTP
  exception) is compiled only into `-p:WinBrewTestHooks=true` builds;
  release builds ignore the config flag and `WINBREW_TEST_MODE`, and
  `doctor` reports them (6.5, 6.6).
- Self-update release keys use the D6 root chain; the compiled-in key is
  only the initial root and rotation follows N→N+1 (6.4).
- `install` rejects `<package>@<version>` together with `--version`
  (exit 2) (6.7); `--dry-run` never refreshes metadata (6.8).
- Offline mode accepts only metadata that was fully verified while valid
  and shows its age (6.9).
- PATH: missing `HKCU\Environment\Path` is created as `REG_EXPAND_SZ`;
  refuse (exit 6) above 32,767 characters, warn above 2,047 (6.10).
- The same-volume check covers `staging\`, `packages\`, `cache\` and
  `bin\` (6.11).
- Exit code `130` is kept and its deviation from the Windows convention
  (`0xC000013A`) is documented in `cli-reference.md` (6.12).

---

> **Note (v2.3):** the locations below refer to v2.3 sections. A- and
> D-sections live in `PROMPT.md`; "Phase 0" and "Phase A–F" live in
> `phases/phase-0.md` … `phases/phase-f.md`. Section numbering is
> unchanged from v2.2 except where the v2.3 changelog above rewrites
> specific rules.

# APPENDIX — Review traceability map

Every finding from "Review: WinBrew .NET Implementation Prompt v2.1"
(2026-11-02) and where v2.3 resolves it.

| Review item | Severity | Resolved in | Resolution |
|---|---|---|---|
| 3.1 Fast path cannot prevent native extraction | 🔴 Critical | A4, A14, A16, A17, Phase 0/B/F | Publish with `IncludeNativeLibrariesForSelfExtract=false`; `wbrew.exe` + `e_sqlite3.dll` zip; fast path re-justified; extraction asserted absent by test; anchor made lazy |
| 3.2 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` has no setter | 🔴 Critical | A4, A6, A14 | Extraction design removed entirely; no env var, no `extract\` directory, no "(or pre-warms…)" alternative |
| 3.3 Catalog index conflicts with pure resolver | 🔴 Critical | D2, D6, A8, A10, Phase D | Sparse index (`dependencies[]`, `architectures[]` per version); resolver reads only catalog; manifest-vs-index exact match (exit 5); `latestVersion` removed; one-transaction ingest; `update`/`repo add`/`repo remove` take the lock; `trust.maxMetadataBytes`; search semantics defined |
| 3.4 PE machine-type rule rejects x86/AnyCPU | 🔴 Critical | A9, A10, A11, A16, A19, Phase 0/C | New accepted-type table (adds `0x014C` on both hosts, ARM64X/ARM64EC noted); ARM32/unknown rejected; host via `IsWow64Process2`/`OSArchitecture`; `x86` artifact with explicit selection order; doctor arch warning |
| 3.5 Re-ACLing attacker-created directories | 🔴 Critical | A12, A16, Phase E | Helper refuses untrusted/reparse directories; atomic creation with final SD; helper-generated `<op>`; hash from `FileShare.Read`-only held handle; no recursive ACLs; refusal tests |
| 4.1 `argv[0]` separator/parser/overflow bugs | 🟠 Important | D4, A16, A18 | Space inserted for non-whitespace-led tails; MSVC C-runtime parser chosen + ADR; 32,767 check → exit 206; empty command line → empty tail; corpus extended |
| 4.2 Live-upgrade shim rules | 🟠 Important | D4, Phase B | Sidecar `FileShare.Read \| FileShare.Delete`, closed before `CreateProcessW`; ownership from DB not hash; `.old.<guid>` journaled, cleanup by record only; target must be inside `packages\` |
| 4.3 Lock metadata edge cases | 🟠 Important | D5, A16, Phase A | `processStartTime` recorded/checked; torn reads = "holder unknown"; no stale-lock breaking (OS frees); backoff polling |
| 4.4 Curve OID check unreliable on CNG | 🟠 Important | D6, A13, A16, Phase D | SPKI pre-parsed via `System.Formats.Asn1` (algorithm OID, exact named-curve params, uncompressed point); `KeySize==256`; 64-byte signatures; duplicate key IDs rejected; key counted once |
| 4.5 PubGrub decision heuristic | 🟠 Important | D2, Phase C | "Active constraints" defined; package choice vs version choice separated; determinism required, perf not; fewest-versions allowed with ordinal tie-break; explanations use qualified IDs and original ranges |
| 4.6 Per-RID lock files | 🟠 Important | A4, A18, Phase A | `RuntimeIdentifiers` on Cli/Shim (helper in v1.1); lock files regenerated on change; CI `--locked-mode` per RID |
| 4.7 System.CommandLine anchor implies early DI | 🟠 Important | A4, Phase A | Lazy `Lazy<IServiceProvider>` anchor; action return = exit code; central `ExitCodes.cs` mapping; default exception handler disabled |
| 5.1 `persist` exclusion invisible to authors | 🟡 Minor | A3, A9, A18 | Author-guide section; optional `writesToInstallDir` manifest field; `info` warning |
| 5.2 Sub-session completeness undefined | 🟡 Minor | Part B, C1 | Phase complete only after test sub-session passes; mid-phase checkpoint marked INCOMPLETE |
| 5.3 "~25 files" threshold vs Phase A/B size | 🟡 Minor | Part B | src/tests split is the default for Phases A and B |
| 5.4 "Cold start" mislabel | 🟡 Minor | A17, C2 | Renamed **warm start**; separate **first-run** metric, reported with no target |
| 5.5 Search target has no measurement environment | 🟡 Minor | A17, Phase D | CI runner, generated 10,000-package catalog, median of 10 runs |
| 5.6 v2.1 changelog disputes (C2, C3, C12, C13) | 🟡 Minor | Changelog | Correction section added; v2.2 changelog states the disputed claims and the replacements |
| 6.1 Inherited Ctrl+C ignore flag | v2.0 | D4, Phase B | Handler routine returning `TRUE`; `SetConsoleCtrlHandler(NULL, TRUE)` banned |
| 6.2 Job object kills breakaway grandchildren | v2.0 | D4, Phase B | `JOB_OBJECT_LIMIT_BREAKAWAY_OK` allowed; no-job fallback logged; documented |
| 6.3 msiexec 1618 mapped to 7 | v2.0 | D9, A12, Phase E | New exit **13 `InstallerBusy`** |
| 6.4 No release-key rotation path | v2.0 | A14, Phase D | Release feed uses the D6 root chain; compiled-in key is initial root; N→N+1 rotation |
| 6.5 Test mode can enable HTTP in production | v2.0 | A11, A13, D10 | Localhost exception compiled only with `-p:WinBrewTestHooks=true`; release builds ignore both settings; doctor reports them |
| 6.6 Crash tests need fault-injection build vs E2E release binary | v2.0 | A16 | Crash tests run in integration with the test-hooks build; E2E covers recovery via real interruption only |
| 6.7 `@version` and `--version` both allowed | v2.0 | A8 | Exit 2 when both present |
| 6.8 Dry-run metadata refresh ambiguous | v2.0 | A8 | Dry-run uses the existing catalog, never updates; missing/expired → exit 3 or 5 with advice |
| 6.9 Offline could accept never-valid metadata | v2.0 | A15 | Only metadata verified while valid is accepted offline; age shown |
| 6.10 PATH missing value / length limits | v2.0 | A12 | Missing value created as `REG_EXPAND_SZ`; refuse (exit 6) > 32,767; warn > 2,047 |
| 6.11 Same-volume check too narrow | v2.0 | A6 | Extended to `cache\` and `bin\` |
| 6.12 Exit 130 vs Windows convention | v2.0 | D9, A18, Phase F | 130 kept; deviation documented in `cli-reference.md` |

---

## Appendix B — v2.2 review verification and traceability (18 findings)

**Verdict: 18/18 confirmed** against the v2.2 text. Two criticals
(3.2, 3.4) originate in v2.1-review recommendations (items 4.2, 4.1);
minor 16 is a v2.1-review omission the reviewer restored.

| Review § | Sev | Verdict | Evidence in v2.2 | Fix in v2.3 |
|---|---|---|---|---|
| 3.1 | 🔴 | Confirmed | A4: zip "containing exactly `wbrew.exe` and `e_sqlite3.dll`"; A14 copies "both" files; D4 copies shims from a template and hash-compares against `WinBrew.Shim.exe` — neither ever shipped | Zip ships `wbrew-shim.exe`; template `app\<ver>\wbrew-shim.exe` copied by `self install`/`self update`; doctor hash check; signed + in `SHA256SUMS.txt` (A4, A6, A14, A18, D4) |
| 3.2 | 🔴 | Confirmed | D4: target "inside … `packages\`", refuse with 9009; A14 self-shim targets `app\<ver>\wbrew.exe`; root "or `WINBREW_ROOT` when set" | `wbrew.shim.json` exception → `app\`; root = parent of the shim's directory; shim never reads `WINBREW_ROOT`; trailing-separator normalization + reparse refusal (D4) |
| 3.3 | 🔴 | Confirmed | A13: localhost exception compiled out of release builds; A16: E2E uses the published release binary; only fixture server defined is local HTTP (Kestrel) | E2E HTTPS-only with per-job test CA in `LocalMachine\Root`, `always()` removal, dev opt-in switch; sanity test refuses `http://localhost` (A13, A16) |
| 3.4 | 🔴 | Confirmed | D4 declares C-runtime toggle semantics, then specifies the boundary-based scan and separator insertion; A16 corpus "asserts the inserted space" | Quote-toggle algorithm verbatim; backslashes literal; no separator insertion; debug assertion; corpus updated to CRT outcomes (D4, A16) |
| 4.1 | 🟠 | Confirmed | A9: `"<id or repo/id>"` accepted; the exit-2 ambiguity rule covers user input only | Unqualified dep ID = declaring repository; cross-repo fully qualified, exit 3 if unconfigured; D2 determinism note; Phase C test (A9, D2, Phase C) |
| 4.2 | 🟠 | Confirmed | D2/D6: resolver reads only the SQLite catalog, filled by verified ingest; ingest + signing arrive in Phase D; Phase B installs with no catalog | Phase B ingests unsigned `index.json` via the final ingest code; `IMetadataVerifier` seam with `TestHooksUnsignedVerifier` compiled out of release; Phase D real verifier + reflection test (Phases B, D) |
| 4.3 | 🟠 | Confirmed | A12: existing-directory check is owner-only; `FileSystemAclExtensions.Create(DirectoryInfo, DirectorySecurity)` no-ops when the directory exists; path-based reparse checks | DACL check (write/append/delete/`WRITE_DAC`/`WRITE_OWNER` for non-Admins/SYSTEM); `CreateDirectoryW` + `ERROR_ALREADY_EXISTS` re-check; explicit owner; opened-handle component checks; Phase E tests (A12, Phase E) |
| 4.4 | 🟠 | Confirmed | A8 defines expired-catalog behavior only for `--dry-run` (exit 3/5); A15: "Online installs never accept expired metadata" with no defined behavior | Auto-refresh on online mutating commands (lock + plan note); read-only serve expired-once-valid with warning; never-valid → exit 3 (A8, A15) |
| 4.5 | 🟠 | Confirmed | A4/A14: "exactly `wbrew.exe` and `e_sqlite3.dll`" hard-coded | Publish output recorded per RID in ADR + committed `scripts/publish-files.txt`; zip = that set minus `.pdb` + `wbrew-shim.exe`; CI drift test (A4; Phases A, F) |
| 4.6 | 🟠 | Confirmed | Single 90 KB file; Part A ≈ 60 KB with changelog, correction section and appendix sent to every session; rejected designs narrated in the changelog | Split into `PROMPT.md` / `phases/*.md` / `CHANGES.md` / `RATIONALE.md`; rationale trimmed from rules (whole document) |
| 4.7 | 🟠 | Confirmed | Phase 0 #4 records Trusted Signing as decided, no eligibility check; A18 "Azure Trusted Signing by default" | Eligibility-conditional route (service now "Artifact Signing", formerly Trusted Signing); OV/EV fallback; UNSIGNED = known limitation + release blocker in C2 (A18, C2, Phase 0) |
| 12 | 🟡 | Confirmed | A9: "`x86` is allowed only when the package has no native artifact" — host-dependent, unverifiable by a manifest validator | Sentence removed; the A10 order governs (A9) |
| 13 | 🟡 | Confirmed | A10 has a selection order but no re-selection rule for upgrades | Re-run selection on every upgrade; plan shows `x64 → arm64`; state + lockfiles updated (A10, Phase C) |
| 14 | 🟡 | Confirmed | D6: search relies on SQLite `LIKE` case-folding (ASCII-only) | `search_text` column via `ToLowerInvariant()` at ingest; query lowercased the same way (D6, Phase D) |
| 15 | 🟡 | Confirmed | D10: the flag is "ignored elsewhere" — `config set` behavior in release undefined | Release builds reject the key with exit 2; `doctor` reports it (D10) |
| 16 | 🟡 | Confirmed | A14/D4: `self update` switches the sidecar; bulk shim replacement is not journaled as one resumable operation | One journaled bulk operation, one fault checkpoint per shim, idempotent, resumable by `recover` (D4, A14) |
| 17 | 🟡 | Confirmed | A16: "real interruption (killing the process)" — no deterministic trigger | Throttled fixture server killed after a fixed byte count or marker file; then `recover`; previous version must work (A16) |
| 18 | 🟡 | Confirmed | Phase 0: "confirm and record them, do not re-open" | Default decisions; Phase 0 records adoption and must report contradicting evidence (Phase 0) |
