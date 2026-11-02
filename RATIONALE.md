# WinBrew Implementation Prompt v2.3 — Decision Rationale

**Audience:** the project owner. Sent only with Phase 0 and Phase F.
Implementation sessions never receive this file (review 4.6): `PROMPT.md`
states the rules with at most one-line reasons; this file explains why
each rule is what it is, and which alternatives were rejected.

## R1 — Publish layout: managed single-file bundle, native DLL side-by-side (A4, A14)

**Decision:** `PublishSingleFile=true`, `SelfContained=true`,
`IncludeNativeLibrariesForSelfExtract=false`; `e_sqlite3.dll` ships
beside `wbrew.exe`; nothing is ever extracted to `%TEMP%`.

**Why:** the .NET host extracts bundled native libraries **before**
`Main` runs, so no in-process fast path can prevent extraction — the
only reliable fix is not to bundle native libraries at all. A
side-by-side native DLL makes load behavior deterministic, observable
and free of `%TEMP%` races and antivirus interference.

**Rejected:** `IncludeNativeLibrariesForSelfExtract=true` (extracts to
a versioned directory under `%TEMP%` or `DOTNET_BUNDLE_EXTRACT_BASE_DIR`
— races, cleanup bugs, AV friction); a "pre-warm" extraction from
`Main` (too late — extraction happens before `Main`); setting the env
var (a console app has no reliable place to set it for itself, and
per-user machine state was a non-goal).

## R2 — Sparse-index catalog and a pure resolver (D2, D6, A10)

**Decision:** `index.json` is a Cargo-style sparse index carrying
`sha256`, `size`, `architectures[]` and `dependencies[]` per version;
the PubGrub resolver consumes only an in-memory snapshot of the SQLite
catalog; selected manifests must exactly match the index entries
(mismatch → exit 5, no changes).

**Why:** keeps the resolver pure and deterministic (no I/O during
resolution), keeps per-invocation work bounded (never re-parse large
JSON), and closes the TOCTOU gap between "what resolution assumed" and
"what the manifest actually says". `latestVersion` was removed because
"latest" is arch- and prerelease-dependent.

## R3 — Shim model: copies of a shipped template (D4, A14)

**Decision:** the zip ships `wbrew-shim.exe` (Native AOT build of
`WinBrew.Shim` for the same RID); `self install`/`self update` copy it
into `app\<ver>\`; every `bin\<name>.exe` is a copy of
`app\<active-ver>\wbrew-shim.exe` plus a sidecar.

**Why:** v2.2's zip contained only `wbrew.exe` + `e_sqlite3.dll`, so
no shim could ever be created and D4's template hash comparison had no
reference — a critical interaction bug introduced by the v2.1 zip fix
(review 3.1). Shipping the template makes `bin\` writes plain file
copies (atomic-ish, no installers), lets `doctor` verify the template
by hash, and keeps one shim binary per architecture. Copies are used
over hard links because entries in `bin\` must survive package
removal, volume moves and user file operations.

## R4 — Sidecar containment and the self-shim exception (D4)

**Decision:** sidecar targets must resolve inside `<root>\packages\`,
except the sidecar named `wbrew.shim.json`, whose target must resolve
inside `<root>\app\`. The root is the parent of the shim's own
directory; the shim never reads `WINBREW_ROOT`.

**Why:** the containment rule (a v2.1-review hardening) otherwise
refused WinBrew's own self-shim, which legitimately targets
`app\<ver>\wbrew.exe` (review 3.2). Keying the exception on the exact
sidecar name keeps every package shim under `packages\`. Deriving the
root from the shim's location (never an env var) prevents an
environment variable from redirecting a security check. The check is
labeled defense in depth: a same-user process can already write to
`packages\` and `bin\`.

## R5 — The shim's `argv[0]` rules (D4, A16)

**Decision:** strip `argv[0]` with the MSVC C-runtime rules — `"`
toggles quoting anywhere, backslashes are literal, `argv[0]` ends at
the first unquoted space or tab; **no separator insertion**; compose
`"\"" + target + "\"" + tail`.

**Why:** Microsoft's documented rules special-case `argv[0]`: quotes
around "parts" of the program name prevent space/tab termination
anywhere in it, and the backslash rules "don't apply" for `argv[0]`
("Parsing C command-line arguments",
https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments).
Consequences: `C:\Pro"gram Fi"les\x.exe a` forwards ` a`;
`"x.exe"arg` parses as `argv[0]` = `x.exearg` and forwards nothing; an
unterminated quote consumes the rest of the line. Because a CRT-parsed
tail is always empty or begins with a space or tab, v2.2's
"separator-insertion" rule was dead code, and its corpus case asserted
the wrong behavior — both removed in v2.3 (review 3.4; v2.2's
algorithm also contradicted its own declared semantics).

**Rejected:** `CommandLineToArgvW` (a different parser: it applies the
2n-backslash rule and treats leading whitespace differently — not what
a C/C++ child process sees); the boundary-based scan (v2.2's
algorithm, which disagreed with the toggle semantics it declared).

## R6 — Elevated helper: refuse, never adopt (A12)

**Decision:** the helper checks owner **and** DACL of existing
directories, refuses on any failure, creates directories via
`CreateDirectoryW` with the final `SECURITY_ATTRIBUTES` and explicit
owner, handles `ERROR_ALREADY_EXISTS` by re-running the checks, and
inspects path components on opened handles
(`FILE_FLAG_OPEN_REPARSE_POINT`).

**Why:** adopting an existing directory is unsafe three ways — open
handles survive DACL changes; recursive re-ACLing follows hard links
to system files; and the attacker who owns the directory can swap it
between a check and a change. `FileSystemAclExtensions.Create(DirectoryInfo,
DirectorySecurity)` silently does nothing when the directory already
exists, so "created atomically" can degrade into "adopted" — hence the
P/Invoke with explicit `ERROR_ALREADY_EXISTS` handling (review 4.3).
Owner-only checks are insufficient: a legacy or misconfigured
Administrators-owned directory can grant `Users:Modify`, which an
attacker can leverage.

## R7 — Interprocess lock: no stale-lock breaking (D5)

**Decision:** the OS releases the lock when the holder dies;
`processStartTime` protects against PID reuse; torn reads mean "holder
unknown"; waiters poll with backoff.

**Why:** the kernel is the only authority on holder liveness; any
user-space "stale lock breaking" is a race. PID alone is unreliable
(reuse), so the start time must match.

## R8 — Trust model: simplified TUF (D6)

**Decision:** ECDSA P-256 + SHA-256, P1363 signatures, exact-payload
envelopes, SPKI pre-parsed with `System.Formats.Asn1`, 64-byte
signature rule, duplicate-key-ID rejection, per-role high-water marks.

**Why:** pre-parsing the SPKI avoids CNG's `Oid.Value` quirk
(`null` with only a `FriendlyName`); exact-byte payloads avoid
canonicalization bugs; high-water marks give replay/rollback
protection; the threshold and duplicate rules close signature-padding
loopholes.

## R9 — Test hooks are compile-time isolated; unsigned-ingest seam (A11, A13, Phases B/D)

**Decision:** all test-only behavior (fault injection, localhost HTTP
exception) is gated on `-p:WinBrewTestHooks=true`; Phase B ingests an
unsigned `index.json` through the final ingest code with signature
verification behind `IMetadataVerifier`, and `TestHooksUnsignedVerifier`
is compiled out of release builds; Phase D swaps in the real verifier.

**Why:** release binaries must be provably incapable of test behavior.
The one clearly named seam (review 4.2) prevents an implementer from
inventing a temporary bypass path around the catalog that survives to
release; the reflection test makes the absence of the seam a checked
property, not an intention.

## R10 — Expired metadata: auto-refresh online, warn-only read-only (A8, A15)

**Decision (review 4.4a):** online mutating commands refresh expired
metadata automatically through the verified `update` path (taking the
lock, noting it in the plan); read-only commands serve
expired-but-once-valid metadata with a warning and never refresh;
`--dry-run` never refreshes; `--offline` accepts only
verified-while-valid metadata and shows its age.

**Why:** `timestamp.json` expires after 7 days, so "fail with exit 5"
(review 4.4b) would make a week-old install break with no
user-visible recovery path — auto-refresh keeps mutating commands
usable while preserving verification. Read-only commands never
refresh to keep them cheap, offline-friendly and side-effect-free.

## R11 — Dependency ID qualification (A9, D2)

**Decision:** an unqualified dependency ID belongs to the declaring
package's repository; cross-repository dependencies must be fully
qualified and resolve only if the target repository is configured
(else exit 3 naming it). Forbidding them entirely in v1.0 remains a
Phase 0 option.

**Why:** an unqualified ID resolved against "whatever repositories are
configured" makes plans non-deterministic across machines (review
4.1). Scoping to the declaring repository is deterministic and needs
no new syntax. Allowing fully-qualified cross-repo references keeps
the format future-proof; the trust concern (two trust roots) is real,
which is why Phase 0 may still forbid them.

## R12 — E2E over HTTPS with a per-job test CA (A13, A16)

**Decision:** E2E generates a throwaway root CA and `localhost` leaf
per job, installs the CA into `LocalMachine\Root` on the disposable
runner, removes it in `always()`, and never touches `CurrentUser\Root`
(dialog blocks non-interactive runs). Dev-machine installs are opt-in
(`scripts/test.ps1 -E2E -InstallTestCA`).

**Why:** v2.2 compiled the plain-HTTP localhost exception out of
release builds (correctly) but then ran E2E against the release binary
over plain HTTP — no E2E install could ever succeed (review 3.3).
HTTPS fixtures keep the release binary strict end to end, and the
sanity test (refuses `http://localhost` even with test mode flags)
proves the exception stayed compiled out.

## R13 — Publish-output manifest (A4, Phase A)

**Decision:** Phase A records the exact publish output per RID in the
ADR and in the committed `scripts/publish-files.txt`; the zip is that
set minus `.pdb` files plus `wbrew-shim.exe`; a CI test fails on
drift.

**Why:** single-file publish output varies with SDK version and
settings; a hard-coded "exactly these files" invites an implementer to
delete a required file to make reality fit the spec (review 4.5). A
recorded manifest turns the zip contents into a checked property.

## R14 — File split (whole document)

**Decision:** `PROMPT.md` (Part A + Part C) is sent to every session;
each `phases/phase-<x>.md` ships alone; `CHANGES.md` and
`RATIONALE.md` are never sent to implementation sessions (RATIONALE
goes to Phase 0 and Phase F).

**Why:** v2.2's single file cost roughly 20k tokens of changelog,
correction and appendix before any work started, undercutting the
point of phased sessions; and narrating rejected designs (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`,
re-ACLing, the old PE rule) risks models reproducing them (review 4.6).
Part A now states what to build; why lives here.

## R15 — Phase 0 defaults instead of settled facts (Phase 0)

**Decision:** the review inputs and the owner-question answers are
recorded as **default decisions** — eight in the Phase 0 list, plus
the System.CommandLine 2.0 stable probe added in v2.3.1 (nine);
Phase 0 adopts each and must report evidence that contradicts any
of them.

**Why:** v2.2 turned the reviewer's open questions into settled facts
the planning phase could not push back on (review minor 18). Defaults
with a duty to report contradictions keep decisions auditable without
re-opening them for sport.

## R16 — Exit-code conventions (D9)

**Decision:** exit 13 `InstallerBusy` for msiexec 1618 after retries;
exit 130 for cancellation, with the deviation from Windows'
`0xC000013A` documented in `cli-reference.md`; exit 206 for shim
command-line overflow.

**Why:** one stable, documented mapping (`ExitCodes.cs`) keeps scripts
and JSON error envelopes honest; the 130 note prevents "bug reports"
from Windows-savvy users while keeping POSIX-familiar ergonomics.

## R17 — Phase order: E precedes F (D1, Phase F; v2.3.1)

**Decision:** the phase order stays 0, A–D, E, F. The first public
release, prepared in Phase F, verifies the v1.0 criteria and lists
the v1.1 additions separately; a portable-only v1.0 build can be cut
by tagging the tree after Phase D.

**Why:** Phase E's fixture installers, helper signing and CI jobs
need the CI and release surface that Phase F builds; running F once,
after E, avoids building the release workflow twice. D1's v1.0/v1.1
split describes feature scope, not build order.
