using WinBrew.Core;
using WinBrew.Infrastructure.Locking;

namespace WinBrew.UnitTests;

/// <summary>
/// In-process D5 lock mechanics. Real cross-PROCESS behavior (holding PID
/// reporting, PID-reuse) lives in the integration tests; here we cover
/// acquire/timeout, holder metadata round-trip, torn-read tolerance and the
/// diagnostic-read share pattern.
/// </summary>
public sealed class LockUnitTests : IDisposable
{
    private readonly string _dir;

    public LockUnitTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wbrew-lock-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    private string LockPath => Path.Combine(_dir, "winbrew.lock");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Acquire_WritesHolderMetadata()
    {
        var mechanism = new ProcessMutationLock(LockPath);

        using var _ = await mechanism.AcquireAsync("config set", TimeSpan.FromSeconds(5), CancellationToken.None);

        // Read with FileShare.ReadWrite — the holder is live and writing
        // (a plain File.ReadAllTextAsync would be denied: it shares only Read).
        using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(CancellationToken.None);
        Assert.Contains("\"command\":\"config set\"", content); // source-gen JSON is compact
    }

    [Fact]
    public async Task SecondWriter_InProcess_TimesOutWithHolderInfo() // D5: kernel rejects second writer
    {
        var mechanism = new ProcessMutationLock(LockPath);
        var handle = await mechanism.AcquireAsync("install", TimeSpan.FromSeconds(5), CancellationToken.None);

        var second = new ProcessMutationLock(LockPath);
        var ex = await Assert.ThrowsAsync<WinBrewException>(() =>
            second.AcquireAsync("upgrade", TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.Equal(WinBrewErrorCode.LockUnavailable, ex.Code);
        Assert.Contains("install", ex.Message);      // blocking command named (D5)
        Assert.Contains($"pid {Environment.ProcessId}", ex.Message); // blocking PID named (A16)
        handle.Dispose();
    }

    [Fact]
    public async Task Release_AllowsImmediateReacquire() // OS frees the lock on handle close
    {
        var mechanism = new ProcessMutationLock(LockPath);
        var first = await mechanism.AcquireAsync("a", TimeSpan.FromSeconds(5), CancellationToken.None);
        first.Dispose();

        using var second = await new ProcessMutationLock(LockPath)
            .AcquireAsync("b", TimeSpan.FromMilliseconds(500), CancellationToken.None);
        Assert.Equal("b", second.Holder.Command);
    }

    [Fact]
    public async Task TornRead_EmptyFile_ReturnsNull_HolderUnknown() // D5 torn-read rule
    {
        await File.WriteAllTextAsync(LockPath, string.Empty);
        Assert.Null(await new ProcessMutationLock(LockPath).ReadHolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TornRead_GarbageBytes_ReturnsNull_NeverThrows() // D5: mid-write observation
    {
        await File.WriteAllBytesAsync(LockPath, [0x7B, 0x22, 0x70, 0x69, 0x00, 0xFF]); // "{\"pi\x00\xFF"
        Assert.Null(await new ProcessMutationLock(LockPath).ReadHolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingFile_ReadHolder_ReturnsNull()
    {
        Assert.Null(await new ProcessMutationLock(Path.Combine(_dir, "absent.lock")).ReadHolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HolderMetadata_CarriesPidCommandAndTimes()
    {
        var mechanism = new ProcessMutationLock(LockPath);
        using var handle = await mechanism.AcquireAsync("self install", TimeSpan.FromSeconds(5), CancellationToken.None);

        var holder = await mechanism.ReadHolderAsync(CancellationToken.None);
        Assert.NotNull(holder);
        Assert.Equal(Environment.ProcessId, holder!.Pid);
        Assert.Equal("self install", holder.Command);
        Assert.False(string.IsNullOrWhiteSpace(holder.StartedAt));
        Assert.False(string.IsNullOrWhiteSpace(holder.ProcessStartTime)); // D5 PID-reuse protection field
    }

    [Fact]
    public async Task Reader_CanReadWhileWriterHolds() // FileShare.Read semantics: diagnostics never block
    {
        var mechanism = new ProcessMutationLock(LockPath);
        using var handle = await mechanism.AcquireAsync("repo add", TimeSpan.FromSeconds(5), CancellationToken.None);

        var holder = await new ProcessMutationLock(LockPath).ReadHolderAsync(CancellationToken.None);
        Assert.NotNull(holder);
        Assert.Equal("repo add", holder!.Command);
    }

    [Fact]
    public async Task Cancellation_RespectedDuringWait()
    {
        var mechanism = new ProcessMutationLock(LockPath);
        using var handle = await mechanism.AcquireAsync("a", TimeSpan.FromSeconds(5), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessMutationLock(LockPath).AcquireAsync("b", TimeSpan.FromSeconds(10), cts.Token));
    }
}
