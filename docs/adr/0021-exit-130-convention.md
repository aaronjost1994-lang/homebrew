# ADR-0021: Exit code 130 for cancellation (D9)

**Status:** accepted (Phase F, 2026-11-06)

## Decision
When WinBrew itself is cancelled (Ctrl+C, a cancelled token, a declined
prompt or UAC dialog, msiexec 1602) it exits **130**, the POSIX convention
(128 + SIGINT). Windows conventionally reports `0xC000013A`
(`STATUS_CONTROL_C_EXIT`) for a console process ended by Ctrl+C; WinBrew
deviates deliberately so scripts can test one value in every shell. A command
run through a shim returns its child's exit code unchanged (the shim forwards
Ctrl+C to the child and never rewrites its status).

## Consequences
Documented in `cli-reference.md`; `ExitCodesTests` pins the value.
