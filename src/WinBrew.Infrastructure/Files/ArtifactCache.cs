using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Infrastructure.Files;

/// <summary>
/// Content-addressed artifact cache (A6, A11 step 4): cache\artifacts\&lt;sha256&gt;.
/// Only verified files enter it (moved from staging on the same volume);
/// every reuse re-hashes the entry and deletes it if it no longer matches.
/// </summary>
public sealed class ArtifactCache(string artifactsDirectory, IPackageFileSystem files) : IArtifactCache
{
    public string PathFor(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure, $"'{sha256}' is not a lowercase SHA-256 digest.");
        }

        return Path.Combine(artifactsDirectory, sha256);
    }

    public async Task<string?> TryGetVerifiedAsync(string sha256, CancellationToken ct)
    {
        var path = PathFor(sha256);
        var actual = await files.Sha256Async(path, ct);
        if (actual is null)
        {
            return null;
        }

        if (actual != sha256)
        {
            await files.DeleteFileAsync(path, ct); // corrupt entry: never extracted (A11)
            return null;
        }

        return path;
    }

    public async Task<string> AddVerifiedAsync(string verifiedFile, string sha256, CancellationToken ct)
    {
        var path = PathFor(sha256);
        await files.CreateDirectoryAsync(artifactsDirectory, ct);

        if (await files.Sha256Async(path, ct) == sha256)
        {
            await files.DeleteFileAsync(verifiedFile, ct);
            return path;
        }

        await files.MoveFileAsync(verifiedFile, path, overwrite: true, ct);
        return path;
    }
}
