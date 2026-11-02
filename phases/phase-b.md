# WinBrew Implementation Prompt v2.3 — Phase B (Secure portable package lifecycle)

> **Read `PROMPT.md` first** — it is sent in the same session and
> contains Part A (the design you must implement) and Part C (the
> checkpoint templates you must fill). This file adds only what is
> specific to this phase. `CHANGES.md` is reference material for the
> project owner and is never sent to implementation sessions.

Each phase session must:
1. Start by restating which decisions from Part A it touches, and any
   assumptions it makes.
2. Implement only its scope, in buildable steps.
3. **Context-window management:** **Phases A and B split into `src` and
   `tests` sub-sessions by default** (this is the plan, not an
   exception); any other phase splits the same way if it requires more
   than ~25 files or approaches context limits. In every split,
   complete all production code (`src/*` and `schemas/*`) first, verify
   `dotnet build` passes, and emit a mid-phase checkpoint report so
   `tests/*` can be completed in the immediate continuation session
   without truncating files. A mid-phase checkpoint is marked
   **INCOMPLETE** (Part C1); **a phase counts as complete only after
   its test sub-session passes and the final checkpoint says
   COMPLETE.**
4. Run the available builds and tests and report the real output.
5. End with a checkpoint report (Part C1).

## Phase B — Secure portable package lifecycle

**Scope:** HTTP client (retries, timeouts, size limits, manual
redirects, test-hooks-only localhost exception, credential origin
policy, redaction); content-addressed cache; streaming SHA-256; safe
ZIP extraction (every A13 defense); staging and placement; the shim
project (D4) and shim management, including: the D4 `argv[0]` tail
parse (quote-toggle scan, backslashes literal, no separator
insertion), the 32,767-character overflow check (exit 206) and
empty-command-line handling; the Ctrl+C handler routine returning
`TRUE` (never `SetConsoleCtrlHandler(NULL, TRUE)`); the job object
with `BREAKAWAY_OK` and a no-job fallback; sidecars opened with
`FileShare.Read | FileShare.Delete` and closed before
`CreateProcessW`; database-based shim ownership; `.old.<guid>`
journaling; sidecar containment per D4 (targets inside `packages\`,
the `wbrew.shim.json` exception inside `app\`, the containment root
derived from the shim's own location, `WINBREW_ROOT` ignored by the
shim); PATH integration (including missing-value and length-limit
rules); the journal state machine with the test-hooks fault seam;
`install` / `uninstall` / `list` / `info` for **single packages with no
dependencies**; **metadata ingest through the final catalog path** —
an unsigned `index.json` is ingested into the final catalog tables by
the final ingest code, with signature verification behind
`IMetadataVerifier`; the test-hooks build registers
`TestHooksUnsignedVerifier`, which is compiled out of release builds
(real signing arrives in Phase D); `recover`; Restart Manager
reporting; antivirus retries; `self install` for the **zip layout**
(copies `wbrew.exe`, `e_sqlite3.dll` and `wbrew-shim.exe` into
`app\<ver>\`, then creates `bin\wbrew.exe` as a copy of the template
with a sidecar to `app\<ver>\wbrew.exe`; no extraction directory) and
the **extraction verification test** (fresh profile, assert no
extraction directory under `%TEMP%`).

**Acceptance**
- The malicious-archive test corpus is fully rejected (one test per
  defense).
- Corrupt, oversized, truncated and interrupted downloads are rejected,
  and staging is cleaned up.
- Crash tests at every checkpoint, followed by `recover`, end in a
  consistent state.
- The shim quoting corpus passes (both quoted and unquoted `argv[0]`,
  including the embedded-quote case forwarding ` a`, the `"x.exe"arg`
  case forwarding nothing, unterminated-quote, tab, empty-command-line
  and length-overflow cases); exit codes and Ctrl+C are forwarded to
  the child (the child sees Ctrl+C).
- Upgrading a package while its shim is running succeeds without
  `ERROR_SHARING_VIOLATION`, including replacing the shim binary itself
  with `.old.<guid>` journaling.
- PATH tests run against a test registry hive or a disposable user,
  preserve existing entries, and cover the missing-value and
  length-limit rules.
- The extraction verification test passes and its result is recorded
  for the ADR.
- Install reads the catalog only: there is no alternate path from
  manifest to plan (verified by test).
- The `wbrew` self-shim sidecar (target inside `app\`) is accepted;
  any other sidecar targeting `app\` is refused; `..` escapes are
  refused; setting `WINBREW_ROOT` does not change the shim's
  containment root.
