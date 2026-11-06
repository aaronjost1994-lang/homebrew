using System.IO.Compression;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Archive;
using WinBrew.Infrastructure.Http;

namespace WinBrew.Infrastructure.Archive;

/// <summary>
/// ZIP extraction with every A13 defense:
/// <list type="number">
/// <item>Pre-scan of ALL entries before anything is written: entry count,
/// name rules (absolute/drive/UNC/'..', separator ambiguity, device names,
/// ':' streams, trailing dots/spaces, invalid characters), Unix symlink mode
/// bits, encrypted entries, and case-insensitive duplicates (including a
/// file and a directory claiming the same path).</item>
/// <item>Streaming extraction that counts ACTUAL decompressed bytes and
/// aborts on the per-entry ratio, the total ratio or the total size limit —
/// header sizes are never trusted.</item>
/// <item>Containment of every final path (full normalization with a
/// trailing separator) and a post-extraction walk refusing reparse points.</item>
/// </list>
/// Any violation deletes the partial output and throws VerificationFailure
/// (exit 5) with a <c>defense</c> detail naming the rule.
/// </summary>
public sealed class SafeZipExtractor : IArchiveExtractor
{
    private const int BufferSize = 81920;

    public async Task<ExtractionResult> ExtractZipAsync(string zipPath, string destinationDirectory, ArchiveLimits limits, CancellationToken ct)
    {
        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true); // idempotent re-run after a crash
        }

        Directory.CreateDirectory(destinationDirectory);
        try
        {
            return await ExtractCoreAsync(zipPath, Path.GetFullPath(destinationDirectory), limits, ct);
        }
        catch
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }

            throw;
        }
    }

    private static async Task<ExtractionResult> ExtractCoreAsync(string zipPath, string root, ArchiveLimits limits, CancellationToken ct)
    {
        var ignoreCase = OperatingSystem.IsWindows();
        ZipArchive archive;
        var stream = File.OpenRead(zipPath);
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            // The constructor does not own the stream until it succeeds; a
            // leaked handle would keep the (cached) artifact locked on Windows.
            await stream.DisposeAsync();
            throw Reject(null, $"the artifact is not a valid ZIP archive: {ex.Message}", "Corrupt");
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        using (archive)
        {
            var plan = PreScan(archive, root, limits, ignoreCase);

            long total = 0;
            long totalCompressed = 0;
            foreach (var (entry, normalized, isDirectory, fullPath) in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (isDirectory)
                {
                    Directory.CreateDirectory(fullPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                totalCompressed += Math.Max(entry.CompressedLength, 1);
                long entryBytes = 0;

                Stream source;
                try
                {
                    source = entry.Open();
                }
                catch (InvalidDataException ex)
                {
                    throw Reject(ArchiveDefense.Encrypted, $"entry '{normalized}' cannot be read: {ex.Message}");
                }

                await using (source)
                await using (var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
                {
                    var buffer = new byte[BufferSize];
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await source.ReadAsync(buffer, ct);
                        }
                        catch (InvalidDataException ex)
                        {
                            throw Reject(null, $"entry '{normalized}' is corrupt: {ex.Message}", "Corrupt");
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        entryBytes += read;
                        total += read;

                        // Limits on ACTUAL decompressed bytes (A13).
                        if (total > limits.MaxTotalBytes)
                        {
                            throw Reject(ArchiveDefense.TotalSizeExceeded,
                                $"extracted bytes exceed archive.maxTotalBytes ({limits.MaxTotalBytes})");
                        }

                        if (entryBytes > (long)limits.MaxCompressionRatio * Math.Max(entry.CompressedLength, 1))
                        {
                            throw Reject(ArchiveDefense.EntryCompressionRatio,
                                $"entry '{normalized}' expands beyond archive.maxCompressionRatio ({limits.MaxCompressionRatio}:1)");
                        }

                        if (total > (long)limits.MaxCompressionRatio * Math.Max(totalCompressed, 1))
                        {
                            throw Reject(ArchiveDefense.TotalCompressionRatio,
                                $"the archive expands beyond archive.maxCompressionRatio ({limits.MaxCompressionRatio}:1) in total");
                        }

                        try
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                        catch (IOException ex) when (DiskFull.Is(ex))
                        {
                            throw new WinBrewException(WinBrewErrorCode.InsufficientSpace,
                                $"Disk full while extracting '{normalized}': {ex.Message}");
                        }
                    }
                }
            }

            VerifyNoReparsePoints(root);
            return new ExtractionResult(plan.Count, total);
        }
    }

    private static List<(ZipArchiveEntry Entry, string Normalized, bool IsDirectory, string FullPath)> PreScan(
        ZipArchive archive, string root, ArchiveLimits limits, bool ignoreCase)
    {
        var entries = archive.Entries;
        if (entries.Count > limits.MaxEntries)
        {
            throw Reject(ArchiveDefense.TooManyEntries, $"{entries.Count} entries exceed archive.maxEntries ({limits.MaxEntries})");
        }

        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<(ZipArchiveEntry, string, bool, string)>(entries.Count);

        foreach (var entry in entries)
        {
            var check = ArchiveEntryRules.CheckName(entry.FullName);
            if (!check.Ok)
            {
                throw Reject(check.Defense, check.Problem!);
            }

            if (ArchiveEntryRules.HasUnixSymlinkMode(entry.ExternalAttributes))
            {
                throw Reject(ArchiveDefense.SymbolicLink, $"entry '{entry.FullName}' is a Unix symbolic link");
            }

            if (entry.IsEncrypted)
            {
                throw Reject(ArchiveDefense.Encrypted, $"entry '{entry.FullName}' is encrypted");
            }

            var normalized = check.NormalizedPath!;
            var key = ArchiveEntryRules.DuplicateKey(normalized);

            if (check.IsDirectory)
            {
                if (seenFiles.Contains(key))
                {
                    throw Reject(ArchiveDefense.CaseInsensitiveDuplicate, $"'{normalized}' is both a file and a directory");
                }

                seenDirectories.Add(key);
            }
            else
            {
                if (!seenFiles.Add(key) || seenDirectories.Contains(key))
                {
                    throw Reject(ArchiveDefense.CaseInsensitiveDuplicate,
                        $"duplicate entry '{normalized}' (paths compare case-insensitively on Windows)");
                }
            }

            // Every parent of a file is an implicit directory: a file may not shadow it.
            var parts = normalized.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var parentKey = ArchiveEntryRules.DuplicateKey(string.Join('/', parts[..i]));
                if (seenFiles.Contains(parentKey))
                {
                    throw Reject(ArchiveDefense.CaseInsensitiveDuplicate, $"'{string.Join('/', parts[..i])}' is both a file and a directory");
                }

                seenDirectories.Add(parentKey);
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!ArchiveEntryRules.IsContained(root, fullPath, ignoreCase))
            {
                throw Reject(ArchiveDefense.EscapesStagingRoot, $"entry '{entry.FullName}' resolves outside the staging directory");
            }

            plan.Add((entry, normalized, check.IsDirectory, fullPath));
        }

        // A file that was recorded before a later entry claimed it as a parent.
        foreach (var (_, normalized, isDirectory, _) in plan)
        {
            if (!isDirectory && seenDirectories.Contains(ArchiveEntryRules.DuplicateKey(normalized)))
            {
                throw Reject(ArchiveDefense.CaseInsensitiveDuplicate, $"'{normalized}' is both a file and a directory");
            }
        }

        return plan;
    }

    /// <summary>Post-extraction walk (A13): no reparse point anywhere under the staging root.</summary>
    private static void VerifyNoReparsePoints(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };

        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", options))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw Reject(ArchiveDefense.ReparsePoint, $"'{Path.GetRelativePath(root, path)}' is a reparse point");
            }

            if (!ArchiveEntryRules.IsContained(root, Path.GetFullPath(path), OperatingSystem.IsWindows()))
            {
                throw Reject(ArchiveDefense.EscapesStagingRoot, $"'{path}' is outside the staging directory");
            }
        }
    }

    private static WinBrewException Reject(ArchiveDefense? defense, string problem, string? name = null) =>
        new(WinBrewErrorCode.VerificationFailure,
            $"Archive rejected: {problem} (A13).",
            new Dictionary<string, object?> { ["defense"] = name ?? defense?.ToString() });
}
