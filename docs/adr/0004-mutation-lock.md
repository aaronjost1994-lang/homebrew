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
- Integration tests (A16) cover, with a real child process
  (`WinBrew.LockHolderHelper`): blocked process reports the holding PID and
  command on timeout; torn/partial metadata = "holder unknown"; reused PID
  with a mismatched start time is not reported as the holder. All pass on
  the Linux dev host as well as (expected) Windows.

### Non-Windows guard file (dev/test only)

- Probed (2026-11-05): .NET's Unix `FileShare` emulation treats only
  `FileShare.None` as exclusive — the D5 writer triple does NOT exclude a
  second writer on Linux/macOS. Writers therefore additionally take an
  exclusive non-blocking `flock` on a separate **guard file**
  (`state\winbrew.lock.guard`), which per-open-file-description semantics
  serialize writers within AND across processes. Diagnostic readers never
  open the guard, so they are never blocked (their own opens take shared
  internal locks that would collide with a holder-side flock on the main
  file — the first design tried; do not repeat it). Windows keeps pure D5
  behavior: no guard file is ever created there.
- A stale guard file left on disk is harmless: its flock is released by the
  OS when the holder's handle closes.
