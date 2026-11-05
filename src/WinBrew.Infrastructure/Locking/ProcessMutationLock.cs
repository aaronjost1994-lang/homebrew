using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Infrastructure.Locking;

/// <summary>Metadata written into state\winbrew.lock by the holder (D5).</summary>
public sealed record LockMetadata(int Pid, string Command, string StartedAt, string ProcessStartTime);

/// <summary>A held mutation lock. Disposing closes the handle — the OS releases the lock.</summary>
public sealed class ProcessLockHandle : ILockHandle
{
    private readonly FileStream _stream;
    private readonly FileStream? _guardStream; // non-Windows writer-writer guard (see ProcessMutationLock)

    internal ProcessLockHandle(FileStream stream, LockHolderInfo holder, FileStream? guardStream)
    {
        _stream = stream;
        Holder = holder;
        _guardStream = guardStream;
    }

    public LockHolderInfo Holder { get; }

    public void Dispose()
    {
        if (_guardStream is not null)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                ProcessMutationLock.TryUnlockFlock(_guardStream.SafeFileHandle);
            }

            _guardStream.Dispose();
        }

        _stream.Dispose(); // closing the handle releases the OS lock (D5)
    }
}

/// <summary>
/// The global mutation lock (D5). Mechanics:
/// - state\winbrew.lock opened with FileMode.OpenOrCreate,
///   FileAccess.ReadWrite, FileShare.Read; every writer asks the same
///   combination, so on WINDOWS the kernel arbitrates: a second writer
///   blocks, while diagnostic readers (FileAccess.Read,
///   FileShare.ReadWrite) proceed.
/// - Non-Windows hosts (dev/test): .NET's Unix FileShare emulation only
///   treats FileShare.None as exclusive (probed 2026-11-05: a second
///   writer with Share.ReadWrite opens successfully; readers with any
///   read-allowing share also open, because their internal shared flock
///   coexists). Writer-writer exclusion therefore comes from an explicit
///   exclusive non-blocking flock on a SEPARATE guard file
///   (state\winbrew.lock.guard) that diagnostic readers never open —
///   so readers stay unblocked while writers serialize. The guard is a
///   non-Windows implementation detail; Windows behavior is pure D5.
/// - On acquire the holder truncates and writes a single-line UTF-8 JSON
///   object and flushes it. Torn reads (empty/unparseable) mean "holder
///   unknown" — never an error.
/// - No stale-lock breaking: a dead holder's handle is closed by the OS.
/// - Waiting uses bounded exponential backoff with jitter, never a busy
///   loop; on expiry the waiting process reads the holder metadata and
///   exits 7 (LockUnavailable) naming PID and command.
/// </summary>
public sealed class ProcessMutationLock : IMutationLock
{
    private readonly string _lockFilePath;

    private const int BaseDelayMs = 50;
    private const int MaxDelayMs = 500;

    // flock operations (Linux/macOS advisory locks)
    private const int LockExclusive = 2; // LOCK_EX
    private const int LockNonBlocking = 4; // LOCK_NB
    private const int LockUn = 8; // LOCK_UN

    public ProcessMutationLock(string lockFilePath)
    {
        _lockFilePath = lockFilePath;
    }

    private string GuardPath => _lockFilePath + ".guard";

    public async Task<ILockHandle> AcquireAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            FileStream? stream;
            try
            {
                var dir = Path.GetDirectoryName(_lockFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                stream = new FileStream(
                    _lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read); // D5: exactly this triple, for every writer
            }
            catch (IOException)
            {
                stream = null; // another writer holds it — back off (Windows)
            }

            FileStream? guard = null;
            var acquired = stream is not null && TryTakeGuard(stream, out guard);

            if (acquired && stream is not null)
            {
                try
                {
                    var holder = await WriteHolderMetadataAsync(stream, command, ct).ConfigureAwait(false);
                    return new ProcessLockHandle(stream, holder, guard);
                }
                catch
                {
                    // Never leak the held lock on a metadata-write failure.
                    stream.Dispose();
                    if (guard is not null)
                    {
                        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                        {
                            TryUnlockFlock(guard.SafeFileHandle);
                        }

                        guard.Dispose();
                    }

                    throw;
                }
            }

            stream?.Dispose();
            guard?.Dispose();

            if (deadline.Elapsed >= timeout)
            {
                throw await TimeoutAsync(command, ct).ConfigureAwait(false);
            }

            var delay = NextBackoff(attempt++);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Non-Windows: take the exclusive guard flock (per-open-file-description,
    /// so it serializes writers within AND across processes). Windows: the
    /// FileShare triple already arbitrated the open — no guard file exists.
    /// </summary>
    private static bool TryTakeGuard(FileStream stream, out FileStream guard)
    {
        if (OperatingSystem.IsWindows())
        {
            guard = null!;
            return true; // a successful open with the D5 triple IS the lock
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            FileStream candidate;
            try
            {
                var guardPath = stream.Name + ".guard";
                // On Linux the .NET FileShare emulation gives write-access
                // handles an exclusive internal lock, so a second guard open
                // is denied HERE; on macOS the flock below decides. Either
                // denial means "held".
                candidate = new FileStream(guardPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                guard = null!;
                return false;
            }

            var fd = (int)candidate.SafeFileHandle.DangerousGetHandle();
            if (Flock(fd, LockExclusive | LockNonBlocking) == 0)
            {
                guard = candidate;
                return true;
            }

            candidate.Dispose();
            guard = null!;
            return false;
        }

        // Other platforms: no advisory-lock fallback available.
        guard = null!;
        return true;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "flock")]
    private static extern int Flock(int fd, int operation);

    internal static void TryUnlockFlock(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            _ = Flock((int)handle.DangerousGetHandle(), LockUn);
        }
    }

    public async Task<LockHolderInfo?> ReadHolderAsync(CancellationToken ct)
    {
        string? content;
        try
        {
            // Diagnostic read: FileAccess.Read with FileShare.ReadWrite (D5).
            // The guard file is NEVER opened here — readers are never blocked
            // by the holder's exclusive flock (non-Windows).
            using var stream = new FileStream(_lockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null; // mid-truncate or mid-write — holder unknown (D5)
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize(content, InfrastructureJsonContext.Default.LockMetadata);
            if (metadata is null)
            {
                return null;
            }

            return new LockHolderInfo(metadata.Pid, metadata.Command, metadata.StartedAt, metadata.ProcessStartTime);
        }
        catch (JsonException)
        {
            return null; // torn/partial write — holder unknown, keep calm (D5)
        }
    }

    // ---- internals -------------------------------------------------------

    private static async Task<LockHolderInfo> WriteHolderMetadataAsync(FileStream stream, string command, CancellationToken ct)
    {
        var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var start = process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        stream.SetLength(0); // truncate first — torn reads are tolerated by readers
        var metadata = new LockMetadata(process.Id, command, now, start);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, InfrastructureJsonContext.Default.LockMetadata);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        return new LockHolderInfo(metadata.Pid, metadata.Command, metadata.StartedAt, metadata.ProcessStartTime);
    }

    private async Task<WinBrewException> TimeoutAsync(string command, CancellationToken ct)
    {
        var holder = await ReadHolderAsync(ct).ConfigureAwait(false);
        var holderText = holder is null
            ? "holder unknown (lock metadata unreadable)"
            : $"pid {holder.Pid} ({holder.Command ?? "unknown command"})";

        return new WinBrewException(
            WinBrewErrorCode.LockUnavailable,
            $"Timed out after waiting for the WinBrew mutation lock while running '{command}'. " +
            $"Another WinBrew process holds it: {holderText}. (D5: no stale-lock breaking — " +
            "the lock frees itself when the holder exits.)",
            new Dictionary<string, object?>
            {
                ["holder"] = holder is null ? null : new Dictionary<string, object?>
                {
                    ["pid"] = holder.Pid,
                    ["command"] = holder.Command,
                    ["startedAt"] = holder.StartedAt,
                },
            });
    }

    private static TimeSpan NextBackoff(int attempt)
    {
        var exponential = Math.Min(BaseDelayMs * (1 << Math.Min(attempt, 6)), MaxDelayMs);
        var jitter = Random.Shared.Next(exponential / 4 + 1);
        return TimeSpan.FromMilliseconds(exponential + jitter);
    }
}
