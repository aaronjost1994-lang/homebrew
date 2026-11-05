# ADR-0004: Mutation lock mechanics (D5)

**Status:** accepted (Phase A src sub-session, 2026-11-05)

## Decision

`ProcessMutationLock` implements D5 exactly:

- **File triple:** `FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read`
  — every writer requests this combination, so the kernel arbitrates: a
  second writer's open fails (sharing violation) while diagnostic readers
  (`FileAccess.Read, FileShare.ReadWrite`) always succeed.
- **Holder metadata:** on acquire, truncate then write one line of UTF-8 JSON
  `{"pid":…,"command":…,"startedAt":…,"processStartTime":…}` and flush. Torn
  reads (empty/unparseable) mean "holder unknown" — readers never error on
  them (D5).
- **`processStartTime` source:** `Process.GetCurrentProcess().StartTime`
  (UTC, "O" format). On Windows this wraps `GetProcessTimes` — the same
  clock the D5 spec names; cross-platform `Process.StartTime` keeps the
  development/test story honest without a P/Invoke duplication.
- **Doctor holder check:** holder is "running" only when BOTH pid resolves
  and the recorded start time matches the live process within a 1.5 s
  tolerance (same clock, formatting round-trip). Missing start time = no
  match, per D5.
- **No stale-lock breaking** — implemented by omission, and stated in the
  timeout error so users understand the lock frees itself when the holder
  exits.
- **Wait loop:** bounded exponential backoff with jitter, 50 ms base,
  doubling capped at 500 ms, ±25 % jitter — never a busy loop; `Task.Delay`
  honors cancellation. On timeout: read holder metadata for the message and
  throw `WinBrewException(LockUnavailable)` → exit 7.

## Consequences

- The lock file persists after a clean release (metadata remains, handle
  closed). That is by design: the next holder truncates; doctor reports
  leftover metadata as "stale" (Ok) using the PID+start-time rule.
- Integration tests (tests sub-session) must cover: blocked process reports
  the holding PID; torn/partial metadata = "holder unknown"; reused PID with
  mismatched start time is not reported as holder (A16).
