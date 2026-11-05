# ADR-0007: Shim — argv[0] parser, sidecar containment, process model (D4)

**Status:** accepted (Phase B src sub-session, 2026-11-06). Windows-observed
behavior (AOT publish, Ctrl+C, piping, job objects) is **NOT RUN** on the
Linux dev host and is recorded as a Windows-CI deliverable.

## Decisions

1. **argv[0] parser = the MSVC C-runtime rule, not `CommandLineToArgvW`.**
   `src/WinBrew.Shim/ShimRules.cs#Tail` scans `GetCommandLineW()` exactly as
   D4's pseudocode: `"` toggles quoting anywhere in argv[0], backslashes
   are literal, the first space/tab outside quotes ends argv[0]. The tail is
   returned verbatim (empty or whitespace-led) — no separator insertion, no
   re-parsing, no re-quoting. Consequences fixed by the corpus:
   `C:\Pro"gram Fi"les\x.exe a` → ` a`; `"x.exe"arg` → `` (argv[0] is
   `x.exearg`); `"x.exe` (unterminated) → ``; `x.exe<TAB>-v` → `<TAB>-v`;
   empty/null command line → ``. Test-hooks builds `FailFast` if the tail
   ever violates the empty-or-whitespace invariant (the D4 debug assertion).
2. **Child command line** = `"\"<target>\"" + tail`, passed as a writable
   NUL-terminated buffer to `CreateProcessW` with `lpApplicationName =
   target`. **Length rule:** the composed string plus its terminating NUL
   must fit in 32,767 characters (`composed.Length + 1 > 32767` → one
   stderr line, exit **206**).
3. **Containment root** = the parent of the shim's own directory
   (`Environment.ProcessPath` → `bin\..`). `WINBREW_ROOT` and every other
   environment variable are ignored. Allowed roots: `<root>\packages\`; for
   the sidecar named `wbrew.shim.json` (the self-shim) **only**
   `<root>\app\`. The target must be fully qualified and, after
   `Path.GetFullPath`, start with the allowed root plus a trailing
   separator (case-insensitive). Every path prefix of the target below the
   volume root is checked with `GetFileAttributesW` and refused when it
   carries `FILE_ATTRIBUTE_REPARSE_POINT`. Violation or missing target:
   one stderr line, exit **9009**. This is defense in depth only — it does
   not protect against same-user processes (D4).
4. **Sidecar I/O:** the shim opens `bin\<name>.shim.json` with
   `FileShare.Read | FileShare.Delete`, reads it fully (≤ 64 KiB), and
   closes the handle before `CreateProcessW`. It parses the four string
   members (`target`, `package`, `version`, `subsystem`) with
   `Utf8JsonReader` (reflection-free, AOT-clean). The writer is
   `WinBrew.Core.Shims.ShimSidecar` (source-generated, camelCase).
5. **Console targets:** process created `CREATE_SUSPENDED`, assigned to a
   Job Object with `KILL_ON_JOB_CLOSE | BREAKAWAY_OK`, then resumed. If job
   creation or assignment fails (e.g. an incompatible outer job) the shim
   logs one stderr line and continues without a job. Ctrl+C: the shim
   installs a handler **routine** (an `[UnmanagedCallersOnly]` function
   returning `TRUE`) via a function pointer — never
   `SetConsoleCtrlHandler(NULL, TRUE)`. It waits and returns the child's
   exit code. **GUI targets** (PE subsystem 2, detected at install time
   and stored in the sidecar): start, do not wait, return 0.
6. **Inheritance:** `bInheritHandles = TRUE`, no `STARTF_USESTDHANDLES`,
   `lpEnvironment = NULL`, `lpCurrentDirectory = NULL` — the child gets the
   shim's console/standard handles, environment and working directory the
   way `cmd.exe` launches programs. **To verify on Windows (E2E):**
   stdin/stdout piping through the shim and Ctrl+C reaching the child.
7. **Binary replacement protocol (D4):** a `bin\<name>.exe` that must change
   is **always** renamed aside to `<name>.exe.old.<guid>` (same volume)
   before the new copy is moved in — renaming a running `.exe` is allowed on
   Windows, overwriting it is not. The `.old` path is written to
   `journal_files` **before** the rename. A binary whose SHA-256 already
   equals the active template is never touched (upgrades then change only
   the sidecar). Cleanup deletes only journaled `.old` files of terminal
   operations, retries locked ones later, and never matches by pattern.

## Consequences

- The quoting corpus can run on any host by linking `ShimRules.cs` into the
  unit tests; only the Win32 half (job, Ctrl+C, piping, AOT size < 3 MB)
  needs Windows runners.
- A shim invoked through its `.old.<guid>` name finds no sidecar and exits
  9009 — `.old` copies are never meant to be launched.
