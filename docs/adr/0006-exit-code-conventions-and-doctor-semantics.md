# ADR-0006: Exit-code conventions and doctor semantics (D9, A8)

**Status:** accepted (Phase A src sub-session, 2026-11-05)

## Decisions

1. **130 for cancellation.** Windows conventionally uses `0xC000013A`;
   WinBrew keeps the POSIX `130` for CLI ergonomics, as D9 itself specifies.
   `cli-reference.md` (Phase F) documents the deviation. Operationally:
   `OperationCanceledException` under a cancelled token maps to
   `Cancelled` (130) through the single central mapping
   (`WinBrew.Cli/ExitCodes.cs`).
2. **Central mapping only.** Handlers never map exit codes; the command
   runner and the top-level handler funnel every failure (typed
   `WinBrewException`, cancellation, unexpected exceptions) through
   `ExitCodes`. Parse errors are `UsageError` (exit 2) — System.CommandLine's
   own default exit code (1) is never allowed to leak: errors are inspected
   before `InvokeAsync`.
3. **Doctor exit semantics:** doctor "succeeds" when its checks RAN —
   `ok: true` in the JSON envelope — but exits **1 (GeneralFailure)** when
   any check status is `Fail`. Warnings alone stay at exit 0. Rationale:
   the envelope reports command success; the exit code is the
   script-facing health signal. Documented here for the C2 report and
   cli-reference.md.
4. **NotAvailableInThisBuild:** every A8 command outside the current
   phase's scope exists in the tree (so `--help` documents the real shape)
   and fails through the NORMAL error path with exit 1 and a structured
   `reason: NotAvailableInThisBuild` detail, naming the phase that delivers
   it (phase-a.md requirement).
5. **doctor status in JSON is a string** (`"Ok" | "Warn" | "Fail"` —
   `UseStringEnumConverter`), not a number: the CLI output schema is
   versioned separately (A8) and string enums keep `cli-output.v1`
   self-describing.
