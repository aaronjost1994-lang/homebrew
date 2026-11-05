namespace WinBrew.Application.Ports;

/// <summary>
/// Holder metadata written into state\winbrew.lock (D5).
/// processStartTime protects against PID reuse: doctor reports a holder as
/// running only when BOTH the PID and the recorded start time match the
/// live process.
/// </summary>
public sealed record LockHolderInfo(int? Pid, string? Command, string? StartedAt, string? ProcessStartTime);

/// <summary>A held mutation lock (D5). Disposing releases it.</summary>
public interface ILockHandle : IDisposable
{
    LockHolderInfo Holder { get; }
}

/// <summary>
/// The global mutation lock (D5): held for the duration of every mutating
/// operation, including recovery. No stale-lock breaking — if the holder
/// dies, the OS closes its handle and the lock is free automatically.
/// </summary>
public interface IMutationLock
{
    /// <summary>
    /// Acquires the lock, polling with bounded exponential backoff and jitter
    /// until <paramref name="timeout"/>. On expiry throws
    /// <see cref="WinBrew.Core.WinBrewException"/> (LockUnavailable, exit 7)
    /// whose message includes the blocking PID and command read from the
    /// lock file. Torn/partial metadata means "holder unknown" — keep
    /// waiting, never error (D5).
    /// </summary>
    /// <param name="command">Command name recorded in the lock metadata.</param>
    Task<ILockHandle> AcquireAsync(string command, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Reads the current holder for diagnostics (doctor). Returns null when
    /// the file is missing or its metadata is empty/unparseable ("holder
    /// unknown", D5) — never throws for torn content.
    /// </summary>
    Task<LockHolderInfo?> ReadHolderAsync(CancellationToken ct);
}
