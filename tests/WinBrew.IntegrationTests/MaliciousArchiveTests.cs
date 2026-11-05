using System.IO.Compression;
using WinBrew.Core;
using WinBrew.Core.Archive;
using WinBrew.Infrastructure.Archive;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// A13 malicious-archive corpus against the real extractor: one archive per
/// defense, each fully rejected (exit-5 VerificationFailure naming the
/// defense) with nothing left in the staging directory.
/// </summary>
public sealed class MaliciousArchiveTests : IDisposable
{
    private static readonly ArchiveLimits Defaults = new(MaxEntries: 100, MaxTotalBytes: 10 * 1024 * 1024, MaxCompressionRatio: 200);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wbrew-zip-" + Guid.NewGuid().ToString("N")[..10]);

    public MaliciousArchiveTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<WinBrewException> RejectAsync(byte[] zip, ArchiveLimits? limits = null)
    {
        var zipPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(zipPath, zip);
        var dest = Path.Combine(_dir, "out");
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => new SafeZipExtractor().ExtractZipAsync(zipPath, dest, limits ?? Defaults, CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.VerificationFailure, ex.Code);
        Assert.False(Directory.Exists(dest), "partial output must be removed");
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")), "nothing may escape the staging directory");
        return ex;
    }

    private static string? Defense(WinBrewException ex) => ex.Details["defense"] as string;

    public static TheoryData<string, string> NameCases() => new()
    {
        { "/etc/evil.txt", nameof(ArchiveDefense.AbsolutePath) },
        { "\\evil.txt", nameof(ArchiveDefense.AbsolutePath) },
        { "C:/evil.txt", nameof(ArchiveDefense.DriveLetter) },
        { "c:evil.txt", nameof(ArchiveDefense.DriveLetter) },
        { "//server/share/evil.txt", nameof(ArchiveDefense.UncPath) },
        { "\\\\server\\share\\evil.txt", nameof(ArchiveDefense.UncPath) },
        { "../evil.txt", nameof(ArchiveDefense.DotDotSegment) },
        { "a/../../evil.txt", nameof(ArchiveDefense.DotDotSegment) },
        { "a\\..\\..\\evil.txt", nameof(ArchiveDefense.DotDotSegment) },      // backslash normalized, then re-checked
        { "a/./b.txt", nameof(ArchiveDefense.DotDotSegment) },
        { "a//b.txt", nameof(ArchiveDefense.SeparatorAmbiguity) },
        { "a\\/b.txt", nameof(ArchiveDefense.SeparatorAmbiguity) },
        { "CON", nameof(ArchiveDefense.ReservedDeviceName) },
        { "dir/nul.txt", nameof(ArchiveDefense.ReservedDeviceName) },
        { "COM1.tar.gz", nameof(ArchiveDefense.ReservedDeviceName) },
        { "lpt9/x.txt", nameof(ArchiveDefense.ReservedDeviceName) },
        { "CONIN$", nameof(ArchiveDefense.ReservedDeviceName) },
        { "conout$.log", nameof(ArchiveDefense.ReservedDeviceName) },
        { "file.txt:hidden", nameof(ArchiveDefense.AlternateDataStream) },
        { "dir./x.txt", nameof(ArchiveDefense.TrailingDotOrSpace) },
        { "name ", nameof(ArchiveDefense.TrailingDotOrSpace) },
        { "a<b.txt", nameof(ArchiveDefense.InvalidCharacter) },
        { "what?.txt", nameof(ArchiveDefense.InvalidCharacter) },
        { "tab\there.txt", nameof(ArchiveDefense.InvalidCharacter) },
    };

    [Theory]
    [MemberData(nameof(NameCases))]
    public async Task MaliciousEntryName_IsRejected(string name, string defense)
    {
        var ex = await RejectAsync(ZipBytes.Create([("ok.txt", "fine"), (name, "evil")]));
        Assert.Equal(defense, Defense(ex));
    }

    [Theory]
    [InlineData("A.txt", "a.txt")]
    [InlineData("Dir/File.TXT", "dir/file.txt")]
    [InlineData("x", "x/y.txt")]           // a file and a directory claiming the same path
    [InlineData("x/y.txt", "X")]
    public async Task CaseInsensitiveDuplicates_AreRejected(string first, string second)
    {
        var ex = await RejectAsync(ZipBytes.Create([(first, "1"), (second, "2")]));
        Assert.Equal(nameof(ArchiveDefense.CaseInsensitiveDuplicate), Defense(ex));
    }

    [Fact]
    public async Task UnixSymlinkModeBits_AreRejected()
    {
        var zip = ZipBytes.Create([("link", "/etc/passwd"u8.ToArray())], e => e.ExternalAttributes = unchecked((int)0xA1FF0000));
        Assert.Equal(nameof(ArchiveDefense.SymbolicLink), Defense(await RejectAsync(zip)));
    }

    [Fact]
    public async Task EncryptedEntries_AreRejected()
    {
        var zip = ZipBytes.Create(("secret.txt", "data"));
        SetEncryptedFlag(zip);
        Assert.Equal(nameof(ArchiveDefense.Encrypted), Defense(await RejectAsync(zip)));
    }

    [Fact]
    public async Task TooManyEntries_AreRejected()
    {
        var zip = ZipBytes.Create(Enumerable.Range(0, 5).Select(i => ($"f{i}.txt", "x")).ToArray());
        var ex = await RejectAsync(zip, Defaults with { MaxEntries = 4 });
        Assert.Equal(nameof(ArchiveDefense.TooManyEntries), Defense(ex));
    }

    [Fact]
    public async Task TotalSize_IsEnforcedOnActualBytes()
    {
        var zip = ZipBytes.Create([("a.bin", RandomBytes(6000)), ("b.bin", RandomBytes(6000))], level: CompressionLevel.NoCompression);
        var ex = await RejectAsync(zip, Defaults with { MaxTotalBytes = 10_000 });
        Assert.Equal(nameof(ArchiveDefense.TotalSizeExceeded), Defense(ex));
    }

    [Fact]
    public async Task ZipBomb_PerEntryCompressionRatio_IsEnforcedWhileStreaming()
    {
        var zip = ZipBytes.Create([("zeros.bin", new byte[4 * 1024 * 1024])]); // ~1000:1 deflate
        var ex = await RejectAsync(zip);
        Assert.Equal(nameof(ArchiveDefense.EntryCompressionRatio), Defense(ex));
    }

    [Fact]
    public async Task HeaderClaimingATinySize_NeverYieldsMoreBytesThanTheLimitsAllow()
    {
        // Lie in the headers: claim 10 bytes for a ~1000:1 entry. Observed (.NET 10):
        // the runtime bounds the decompressed stream by the declared size, so the
        // bomb cannot expand; the extractor's limits apply to the bytes actually
        // written either way (A13: never trust header sizes for the limits).
        var zip = ZipBytes.Create([("zeros.bin", new byte[2 * 1024 * 1024])]);
        PatchUncompressedSizes(zip, 10);
        var zipPath = Path.Combine(_dir, "lying.zip");
        await File.WriteAllBytesAsync(zipPath, zip);
        var dest = Path.Combine(_dir, "lying");
        try
        {
            var result = await new SafeZipExtractor().ExtractZipAsync(zipPath, dest, Defaults, CancellationToken.None);
            Assert.True(result.TotalBytes <= 10, $"extracted {result.TotalBytes} bytes from a 10-byte claim");
            Assert.True(new FileInfo(Path.Combine(dest, "zeros.bin")).Length <= 10);
        }
        catch (WinBrewException ex)
        {
            Assert.Equal(WinBrewErrorCode.VerificationFailure, ex.Code);
            Assert.False(Directory.Exists(dest));
        }
    }

    [Fact]
    public async Task NotAZip_IsRejected()
    {
        var ex = await RejectAsync("definitely not a zip"u8.ToArray());
        Assert.Equal("Corrupt", Defense(ex));
    }

    [Fact]
    public async Task ValidArchive_Extracts_WithCounts()
    {
        var zipPath = Path.Combine(_dir, "ok.zip");
        await File.WriteAllBytesAsync(zipPath, ZipBytes.Create([("bin/", []), ("bin/tool.exe", PeBytes.Create()), ("README.md", "hi"u8.ToArray())]));
        var dest = Path.Combine(_dir, "ok");
        var result = await new SafeZipExtractor().ExtractZipAsync(zipPath, dest, Defaults, CancellationToken.None);
        Assert.Equal(3, result.Entries);
        Assert.True(File.Exists(Path.Combine(dest, "bin", "tool.exe")));
        Assert.Equal("hi", await File.ReadAllTextAsync(Path.Combine(dest, "README.md")));
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    /// <summary>Sets general-purpose bit 0 (encrypted) in every local and central header.</summary>
    private static void SetEncryptedFlag(byte[] zip)
    {
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x03 && zip[i + 3] == 0x04)
            {
                zip[i + 6] |= 1;
            }
            else if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
            {
                zip[i + 8] |= 1;
            }
        }
    }

    /// <summary>Overwrites the declared uncompressed size in local (+22) and central (+24) headers.</summary>
    private static void PatchUncompressedSizes(byte[] zip, uint size)
    {
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x03 && zip[i + 3] == 0x04)
            {
                BitConverter.TryWriteBytes(zip.AsSpan(i + 22, 4), size);
            }
            else if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
            {
                BitConverter.TryWriteBytes(zip.AsSpan(i + 24, 4), size);
            }
        }
    }
}
