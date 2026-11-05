# WinBrew — Phase 0 Implementation Plan

**Status: APPROVED** (project owner, 2026-11-05, in the session that
cloned this repository to a new server and began implementation). Nothing
here changes a D-decision; objections are recorded, not applied.

> Implementation note (2026-11-05): the implementation repository was
> created locally as `/opt/winbrew` (not yet pushed to GitHub) and the
> Phase A **src sub-session** completed on a Linux host — see
> `/opt/winbrew/docs/checkpoints/phase-a-src.md` and `/opt/winbrew/docs/adr/`
> for the toolchain deviations this plan sanctions (SDK 10.0.403,
> System.CommandLine 2.0.14, FTS5 available).

**Session disclosure (honesty rules, A2).** This draft was produced
inside a working session that also maintains the prompt file set —
not a fresh session started with exactly `PROMPT.md` +
`phases/phase-0.md` + `RATIONALE.md` — and on a Linux host (a
.NET 10.0.301 SDK exists here, off PATH). Phase 0 requires no
WinBrew builds; the plan's environment assumptions were validated
with a scratch TFM-matrix solution outside this repository — builds,
tests, and Native AOT attempts, evidence in `PROBES.md` §4. If the
owner prefers, `phases/phase-0.md` can
be re-run in a dedicated session using this file and `PROBES.md` as
prior evidence; the deliverables below are structured to survive that.

## 1. Assumptions and open questions

Assumptions (each traced to Part A):
- The implementation repository is a **new** repository (this planning
  repo holds only the prompts; A5's tree is created from scratch in
  Phase A).
- `global.json` pins a .NET 10 SDK band; the exact band is chosen when
  Phase A starts (latest supported at that date).
- Packages, not the manifest author, define `size` limits per artifact;
  client-side `download.maxBytes` (D10) applies on top (A11 step 3).
- Package identity is `<repo>/<id>` (A9); all state tables key on it.

Open questions for the owner (answers do not block Phase A):
1. **When the implementation repository goes public.** Today this
   planning repo is private, and GitHub-hosted Windows ARM64 runners
   are listed for public repositories only (`PROBES.md` §2). If Phase
   F runs while the repo is private, ARM64 E2E reports BLOCKED per
   A16 or uses a self-hosted ARM64 runner.
2. **Trusted Signing identity validation** should be started early:
   it is a real, sometimes slow eligibility gate and blocks the
   signing route decision in Phase F (`PROBES.md` §3).
3. **Trademark check for "WinBrew"** before public publishing (A1
   note) — owner task, not probeable from here.
4. **Self-update feed hosting** (URL + who operates it) — needed by
   Phase D's `self update` and listed in `release-checklist.md` (A19).

Two plan-level proposals requiring owner sign-off (not spec changes):
- **`.old.<guid>` cleanup trigger** (D4 says cleanup "retries later"
  but no command owns the retry): this plan assigns cleanup of
  journaled `.old` files to the end of every successful mutating
  operation (files whose operation reached a terminal state) and to
  `doctor` reporting leftovers. Recorded here so Phase B tests it.
- **Fault-injection occurrence syntax**: several checkpoints fire
  once *per shim* (D4's bulk replace). `WINBREW_FAULT_AT=<name>`
  gets an optional occurrence suffix — `<name>#2` faults at the
  second firing — so per-shim faults are deterministic. Registry
  names themselves stay static (test-enumerable, A11).

## 2. Default decisions — adopted

Each default below is **adopted**. Contradicting evidence is flagged.

1. **Extraction: none expected** with
   `IncludeNativeLibrariesForSelfExtract=false`. Verified by test in
   Phase B (fresh profile, assert no extraction directory under
   `%TEMP%`); recorded in the shim ADR then. *Adopted; no evidence
   either way yet — it is Phase B's test.*
2. **FTS5: probe in Phase A** decides whether `search` uses an FTS5
   index over `search_text` or matches it directly (D6). *Adopted;
   result lands in the Phase A ADR. If FTS5 is present and adopted,
   the index ships as migration v2 (forward-only, A6).*
3. **ARM64 runners: available for public repos, not private ones**
   (`PROBES.md` §2, fetched 2026-11-04: label `windows-11-arm`,
   public preview). This planning repo is private **today**, so
   Phase F on a private repo ⇒ ARM64 E2E **BLOCKED per A16** or
   self-hosted; once the implementation repo is public, the hosted
   runner exists but stays public-preview — re-verified at Phase F,
   and losing it is an A16 BLOCKED case, never a silent skip.
4. **Signing: managed service (Trusted Signing) if the owner
   qualifies**, else OV/EV certificate, else UNSIGNED labeled as a
   release blocker (A18). Contradicting evidence, reported per
   phase-0.md's instruction: **the docs still say "Trusted
   Signing"; the claimed rename to "Artifact Signing" (CHANGES.md
   fix 4.7) could not be confirmed** (`PROBES.md` §3). A18 already
   says "verify its current name", so the prompts need no change;
   re-check in the Azure portal at sign-up. Identity validation is
   the eligibility gate (open question 2).
5. **x86 as explicit last-resort fallback** per the A10 order
   (arm64: `arm64 → x64 → x86`; x64: `x64 → x86`), chosen fallback
   shown in the plan and warned in `--json`. x86 PE images inside
   x64/arm64 artifacts always accepted. *Adopted.*
6. **Expired metadata: mutating commands auto-refresh via the
   verified `update` path; read-only commands serve
   expired-but-once-valid metadata with a warning, never refresh**
   (A8). *Adopted.*
7. **Cross-repo dependencies: allowed fully qualified, resolved only
   if the target repo is configured** (A9). Recommended over the
   forbidding option: the sparse-index model invites community repo
   layering, qualification checks are cheap (store the verbatim
   string, resolve at plan time), and a hard forbid would break it
   in v1.0 with no security gain: unqualified refs are permanently
   scoped to the declaring repository (A9), and cross-repo resolution
   fails closed with exit 3 when the target repo is unconfigured.
   *Adopted; revisit only if a real repo ecosystem asks.*
8. **E2E over HTTPS with a per-job test CA** installed into
   `LocalMachine\Root` on disposable runners (A16). *Adopted; if the
   owner's security policy forbids installing a test root CA, the
   plan reports it back and an alternative trusted-HTTPS approach is
   designed with the owner before Phase B (the spec's own
   requirement; no specific fallback is assumed here).*
9. **System.CommandLine: stable 2.0 GA exists; record 2.0.11**
   (nuget.org, fetched 2026-11-04; latest of `2.0.0`…`2.0.11`;
   3.0 previews are out of scope). The "no stable release" branch of
   the probe is moot; A4's anchor holds. *Adopted.* (PROBES.md §1.)

## 3. Repository tree and reference graph (A5, finalized)

The A5 tree is confirmed buildable as written and adopted verbatim;
this section adds only the edges A5's rules imply:

```
WinBrew.Core           → (nothing; no I/O — A5 rule)
WinBrew.Application    → Core
WinBrew.Infrastructure → Application      (implements its interfaces)
WinBrew.Windows        → Application       (implements its interfaces)
WinBrew.Cli            → Application (+ Infrastructure + Windows
                         as composition root: DI wiring only; command
                         handlers still call one use case each)
WinBrew.Shim           → (nothing; self-contained Native AOT, D4)
WinBrew.ElevatedHelper → Core (+ Windows for ACL/MSI helpers;
                         exposes only the narrow A12 command surface)
tests/*                → the projects they exercise + TestFixtures
```

ElevatedHelper's reuse of `WinBrew.Windows` is a plan-level choice
(own narrow interop was the alternative); flagged for owner review.
One deviation to enforce in Phase A: **Cli is the only project that
may reference Infrastructure and Windows**, and only for
registration — a violation is a review finding.

## 4. Target frameworks per project (R15 deliverable)

| Project | TFM | Reason |
|---|---|---|
| `WinBrew.Core` | `net10.0` | Pure domain; A5 forbids I/O deps. |
| `WinBrew.Application` | `net10.0` | Use cases over ports; no Windows APIs. |
| `WinBrew.Infrastructure` | `net10.0` | HTTP/SQLite/files are cross-plat; keeping it portable forces every Windows behavior into `WinBrew.Windows` where it can be reviewed. |
| `WinBrew.Windows` | `net10.0-windows` | Registry (`HKCU\Environment\Path`, A12), Restart Manager, MSI/EXE (Phase E), ACLs, `IsWow64Process2` P/Invoke. |
| `WinBrew.Cli` | `net10.0-windows` | Composition root above `Windows`; referencing a `net10.0-windows` project from plain `net10.0` is a SDK error, and `Cli` publishes self-contained per A4 anyway. |
| `WinBrew.Shim` | `net10.0-windows`, **Native AOT** | `GetCommandLineW`, `CreateProcessW`, Job Objects, `SetConsoleCtrlHandler` (D4); long-path manifest (A12); RIDs `win-x64`, `win-arm64`. |
| `WinBrew.ElevatedHelper` | `net10.0-windows` | Elevated, ACL and machine-scope work (A12). |
| `WinBrew.UnitTests` | `net10.0` | Domain + use-case only. |
| `WinBrew.IntegrationTests` | `net10.0-windows` | Exercises Windows adapters + real SQLite. |
| `WinBrew.EndToEndTests` | `net10.0-windows` | Real installs on the runner (A16). |
| `WinBrew.TestFixtures` | `net10.0` | Fixture builder, local HTTP server, test keys. |

All publishes set `RuntimeIdentifiers` `win-x64;win-arm64` where they
ship (Cli, Shim, ElevatedHelper). `IsWow64Process2` is P/Invoke and
works from any TFM, but it is only *called* by `WinBrew.Windows`, so
the Windows TFM boundary stays clean (A10 host detection).

## 5. SQLite schema v1 (DDL draft)

WAL mode; `PRAGMA user_version = 1`; foreign keys ON; forward-only
migrations with a `state\backups\` copy before each (A6). Config is
`config.json` (D10), cache is content-addressed files — neither is
duplicated here. Trust **root history files** live under
`repositories\<repo>\trust\` (A6); the database stores only the
accepted-version high-water marks (D6). `catalog_*` rows +
`role_versions` are written in **one transaction** per ingest (D6).

```sql
-- repositories (one row per configured repo)
CREATE TABLE repositories (
  name       TEXT PRIMARY KEY,          -- short name; used in paths (A6)
  url        TEXT NOT NULL,
  created_at TEXT NOT NULL              -- ISO-8601, UTC
);
-- root accepted-version lives in role_versions, like every role (D6)

-- per-role replay protection (D6): reject any version below the mark
CREATE TABLE role_versions (
  repo       TEXT NOT NULL REFERENCES repositories(name) ON DELETE CASCADE,
  role       TEXT NOT NULL CHECK (role IN ('root','timestamp','index')),
  accepted_version INTEGER NOT NULL,
  accepted_at TEXT NOT NULL,
  PRIMARY KEY (repo, role)
);

-- catalog: package summaries (D6 sparse index, resolver's only input)
CREATE TABLE catalog_packages (
  repo        TEXT NOT NULL REFERENCES repositories(name) ON DELETE CASCADE,
  id          TEXT NOT NULL,
  display_name TEXT NOT NULL,
  description TEXT NOT NULL,
  search_text TEXT NOT NULL,            -- ToLowerInvariant(id+name+desc), D6
  PRIMARY KEY (repo, id)
);
CREATE INDEX idx_pkg_search ON catalog_packages(search_text);

-- catalog: version entries (sha256/size = the MANIFEST digest+size
-- from index.json; artifact digests live in manifests, A10/A11)
CREATE TABLE catalog_versions (
  repo        TEXT NOT NULL REFERENCES repositories(name) ON DELETE CASCADE,
  package_id  TEXT NOT NULL,
  version     TEXT NOT NULL,            -- NuGetVersion ToNormalizedString() (D3)
  manifest_sha256 TEXT NOT NULL,
  manifest_size   INTEGER NOT NULL,
  is_prerelease   INTEGER NOT NULL,     -- derived at ingest for
                                        -- "latest" computation (D6)
  PRIMARY KEY (repo, package_id, version),
  FOREIGN KEY (repo, package_id) REFERENCES catalog_packages(repo, id)
              ON DELETE CASCADE
);

CREATE TABLE catalog_architectures (
  repo        TEXT NOT NULL,
  package_id  TEXT NOT NULL,
  version     TEXT NOT NULL,
  arch        TEXT NOT NULL,            -- x64 | arm64 | x86
  PRIMARY KEY (repo, package_id, version, arch),
  FOREIGN KEY (repo, package_id, version) REFERENCES catalog_versions
              ON DELETE CASCADE
);

-- catalog: dependencies VERBATIM from the index (D6); qualification
-- (A9) is resolved at plan time, never rewritten at ingest
CREATE TABLE catalog_dependencies (
  repo        TEXT NOT NULL,
  package_id  TEXT NOT NULL,
  version     TEXT NOT NULL,
  dep_id      TEXT NOT NULL,            -- as written; may be repo-qualified
  dep_range   TEXT NOT NULL,            -- NuGet interval notation, verbatim (D3)
  position    INTEGER NOT NULL,         -- preserves manifest order
  PRIMARY KEY (repo, package_id, version, position),
  FOREIGN KEY (repo, package_id, version) REFERENCES catalog_versions
              ON DELETE CASCADE
);
CREATE INDEX idx_dep_reverse ON catalog_dependencies(dep_id);

-- installed state (A9/A10)
CREATE TABLE installed_packages (
  repo        TEXT NOT NULL,
  package_id  TEXT NOT NULL,
  version     TEXT NOT NULL,
  arch        TEXT NOT NULL,            -- re-selection records the change, A10
  install_type TEXT NOT NULL CHECK (install_type IN ('portable')),
  explicit    INTEGER NOT NULL,         -- 1 requested, 0 pulled as dep (A10)
  installed_at TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  PRIMARY KEY (repo, package_id)        -- one active version per identity
);
CREATE INDEX idx_installed_explicit ON installed_packages(explicit);

CREATE TABLE dependency_edges (         -- every edge, A10
  dependent_repo TEXT NOT NULL,
  dependent_id   TEXT NOT NULL,
  dep_repo       TEXT NOT NULL,
  dep_id         TEXT NOT NULL,
  PRIMARY KEY (dependent_repo, dependent_id, dep_repo, dep_id),
  FOREIGN KEY (dependent_repo, dependent_id)
    REFERENCES installed_packages(repo, package_id) ON DELETE CASCADE
);
CREATE INDEX idx_edges_by_dep ON dependency_edges(dep_repo, dep_id);

CREATE TABLE pins (                     -- cleanup never removes pinned (A10)
  repo        TEXT NOT NULL,
  package_id  TEXT NOT NULL,
  pinned_at   TEXT NOT NULL,
  PRIMARY KEY (repo, package_id),
  FOREIGN KEY (repo, package_id) REFERENCES installed_packages
              ON DELETE CASCADE
);

-- shim ownership lives in the database, never the hash (D4)
CREATE TABLE shims (
  name        TEXT PRIMARY KEY,         -- bin\<name>.exe
  owner_repo  TEXT NOT NULL,
  owner_pkg   TEXT NOT NULL,
  owner_version TEXT NOT NULL,
  target_path TEXT NOT NULL,            -- absolute; containment per D4
  subsystem   TEXT NOT NULL CHECK (subsystem IN ('console','gui')),
  template_sha256 TEXT NOT NULL,        -- doctor compares (D4)
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  FOREIGN KEY (owner_repo, owner_pkg) REFERENCES installed_packages
              ON DELETE CASCADE
);
CREATE INDEX idx_shims_by_owner ON shims(owner_repo, owner_pkg);

-- journal (A11): operations + ordered steps, intent before / outcome after
CREATE TABLE operations (
  op_id       TEXT PRIMARY KEY,         -- = staging\<operation-id> (A6)
  type        TEXT NOT NULL CHECK (type IN
              ('install','upgrade','uninstall','bundle','selfinstall',
               'selfupdate','selfuninstall','repoadd','reporemove')),
  state       TEXT NOT NULL CHECK (state IN
              ('Planned','Downloaded','Verified','Extracted','Validated',
               'Placed','ShimsActivated','StateCommitted','Completed',
               'Compensating','RolledBack','RecoveryFailed')),
  command     TEXT NOT NULL,
  error       TEXT,                     -- exact inconsistency report (A11)
  started_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
CREATE INDEX idx_ops_open ON operations(state)
  WHERE state NOT IN ('Completed','RolledBack','RecoveryFailed');

CREATE TABLE operation_steps (
  op_id       TEXT NOT NULL REFERENCES operations ON DELETE CASCADE,
  seq         INTEGER NOT NULL,
  name        TEXT NOT NULL,            -- checkpoint name (§6)
  intent_json TEXT NOT NULL,            -- written BEFORE the action
  outcome_json TEXT,                    -- written AFTER; NULL = not reached
  state       TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  PRIMARY KEY (op_id, seq)
);

-- every durable file side-effect, including .old.<guid> renames (D4);
-- cleanup deletes ONLY rows here, never pattern matches
CREATE TABLE journal_files (
  op_id       TEXT NOT NULL REFERENCES operations ON DELETE CASCADE,
  seq         INTEGER NOT NULL,
  path        TEXT NOT NULL,            -- absolute, same volume (A6)
  kind        TEXT NOT NULL CHECK (kind IN
              ('old_shim','old_dir','staging','sidecar')),
  created_at  TEXT NOT NULL,
  deleted_at  TEXT,                     -- NULL until cleanup succeeds
  PRIMARY KEY (op_id, seq, path)
);
CREATE INDEX idx_files_pending ON journal_files(path)
  WHERE deleted_at IS NULL;
```

Notes: MSI product tracking is **not** in v1 — Phase E ships a
forward-only migration adding it (v3 if the Phase A FTS5 migration
was adopted, else v2, per §2 item 2). An adopted FTS5 index is itself
a forward-only migration under the same rule (A6/D6). `repo remove`
deletes catalog rows by cascades inside the same transaction that
drops `role_versions` marks and `repositories` — a mutating command
under D5's lock. A newer schema than the binary supports is a clear
exit-1 error (A6).

## 6. Fault-injection checkpoint registry (A11)

Static names, enumerated for tests via the registry; `IFaultInjector.CheckpointAsync(name)`
fires at every durable transition. Per-shim checkpoints fire once per
shim (occurrence syntax per §1).

| Name | Fires at |
|---|---|
| `download.started` | `staging\<op>\` created |
| `download.completed` | stream done, size limits enforced |
| `artifact.verified` | SHA-256 OK |
| `artifact.cached` | moved into `cache\artifacts\<sha256>` |
| `extract.completed` | A13 defenses all passed |
| `validate.completed` | PE machine types + shim collisions OK |
| `package.placed` | renamed into `packages\<id>\<version>\<arch>` |
| `shim.sidecar.replaced` | per shim: `<name>.shim.json` replaced |
| `shim.binary.replaced` | per shim: `<name>.exe` placed (`.old.<guid>` journaled first) |
| `package.activated` | per package: shims + installed state consistent |
| `state.committed` | SQLite transaction committed (upgrade: old intact until here, A11) |
| `operation.completed` | terminal `Completed` row |
| `compensate.started` | entering `Compensating` |
| `compensate.shim.reverted` | per shim: sidecar back at previous version |
| `compensate.package.removed` | placed-but-uncommitted directory removed |
| `rollback.completed` | terminal `RolledBack` row |
| `recovery.failed` | terminal `RecoveryFailed` row (exit 8, A11) |
| `path.updated` | `HKCU\Environment\Path` written + `WM_SETTINGCHANGE` (A12) |
| `catalog.ingested` | the one-transaction ingest committed (D6) |
| `trust.root.accepted` | new root N accepted (rotation walk done, D6) |
| `selfupdate.app.copied` | new `app\<version>\` in place (A14) |

Phase B may add names; additions are backward-compatible (tests
enumerate, nothing hardcodes the count).

## 7. Phase-by-phase plan with estimated file counts

Estimates ±30%; src counts include `csproj`/props/schema JSON where a
phase creates them. Phases A and B split into `src`/`tests`
sub-sessions by default (phase-0.md §3).

| Phase | Scope | src | tests | Split |
|---|---|---|---|---|
| 0 | this plan | 0 | 0 | — |
| A | scaffolding, config, 8 schemas, SQLite+migrations, mutation lock, `doctor`, FTS5 probe | ~34 | ~28 | yes |
| B | HTTP hardening, cache, A13 archive defenses, Native AOT shim + corpus, journal/recovery, `self install`, PATH (A12) | ~28 | ~32 | yes |
| C | PubGrub resolver, arch selection/re-selection, pins, cycles, plan rendering, upgrade compensation | ~15 | ~24 | if large |
| D | ASN.1/ECDSA trust, sparse ingest, `repo add/list/remove`, `update`, bundles, `self update`/`self uninstall` | ~26 | ~26 | if large |
| E | MSI/EXE, elevated helper (refuse-never-adopt), ACL snapshots | ~18 | ~20 | if large |
| F | docs (incl. ADRs from every D-decision), CI/release workflows, SBOM, signing, readiness report | ~14 | ~8 | no |

Order per D1/R17: 0 → A → B → C → D → E → F; a phase completes only
when its test sub-session passes (C1 COMPLETE).

## 8. Objections to D-decisions (recorded, not changed)

1. **D1 order (E before F)** — accepted; the cost is that portable-only
   v1.0 cannot be *released* until E exists, only *tagged*. R17 already
   documents why (one hardening pass over both). No change sought.
2. **D4 cleanup trigger** — "retries later" has no owner command; this
   plan proposes the trigger (§1, first proposal). If the owner
   prefers `recover`-only cleanup, Phase B's tests change, not the D.
3. **D6 signing-name evidence** — the "Artifact Signing" rename in
   CHANGES.md is contradicted by today's docs (`PROBES.md` §3).
   Reported, not edited; A18's own hedge covers it.
4. **D5 lock metadata** — truncate-then-write torn reads are already
   specified ("holder unknown; keep waiting"); plan adds an explicit
   test. No objection.
5. **Default 7 (cross-repo deps)** — adopted with reasoning in §2.7;
   no objection to the default itself.

## 9. Checkpoint (C1, condensed)

- **Status:** COMPLETE for Phase 0's deliverables *as a draft* —
  pending the owner's approval, which is this phase's acceptance gate.
- **Builds/tests run:** no WinBrew builds (Phase 0 is plan-only).
  Environment validation: scratch TFM-matrix builds, xunit runs, and
  Native AOT attempts on .NET 10.0.301/Linux — all pass except
  win-x64 AOT, which is refused off-Windows (cross-OS), so **shim
  binaries and Windows-runtime suites need Windows**. Evidence:
  `PROBES.md` §4, including two NU1903 high-severity advisories the
  NuGet pins must address. External probes are in `PROBES.md` §1–3
  (SCL 2.0.11; ARM64 runner public-only, this repo private today;
  Trusted Signing name unrenamed in docs).
- **Evidence anchors:** A5 (tree/edges), A6 (storage/WAL/migrations),
  A9/A10 (identity, edges, explicit flag, one active version),
  A11 (journal, state machine, checkpoints), A12 (PATH, helper),
  D4 (shim db ownership, `.old` journal), D5 (lock), D6 (roles,
  one-transaction ingest), R15 (TFM deliverable), R17 (order).
- **Next session (Phase A) must start with:** `PROMPT.md` +
  `phases/phase-a.md` + this approved plan as the previous
  checkpoint; Phase A then implements §3–§6 as specified and probes
  FTS5 (§2 item 2).
