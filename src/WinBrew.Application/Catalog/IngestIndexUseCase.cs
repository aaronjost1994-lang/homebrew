using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Catalog;
using WinBrew.Core.Operations;

namespace WinBrew.Application.Catalog;

public sealed record IngestResult(string Repo, int IndexVersion, int Packages, int Versions);

/// <summary>
/// The FINAL catalog ingest path (D6). Phase B feeds it an unsigned
/// index.json through <see cref="IMetadataVerifier"/> (the test-hooks
/// TestHooksUnsignedVerifier; release builds refuse until Phase D signing);
/// Phase D's <c>repo add</c> / <c>update</c> call this same code after the
/// root → timestamp → index chain. Catalog rows and the accepted index
/// version are written in ONE transaction; a lower version is a rollback
/// and is rejected; expired metadata is rejected (trust.maxClockSkewSeconds).
/// Callers hold the mutation lock (D6: update/repo add are mutating).
/// </summary>
public sealed class IngestIndexUseCase(IMetadataVerifier verifier, ICatalogStore catalog, IFaultInjector faults, IConfigStore config, TimeProvider time)
{
    public async Task<IngestResult> ExecuteAsync(RepositoryRecord repository, byte[] indexDocument, long maxMetadataBytes, CancellationToken ct)
    {
        if (!Identifiers.IsValidRepoName(repository.Name))
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, $"'{repository.Name}' is not a valid repository name (A9).");
        }

        if (indexDocument.LongLength > maxMetadataBytes)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                $"index.json is {indexDocument.LongLength} bytes, above trust.maxMetadataBytes ({maxMetadataBytes}, D6).");
        }

        var payload = await verifier.VerifyIndexAsync(repository.Name, indexDocument, ct);
        var index = RepositoryIndexParser.Parse(payload);

        var skew = await GetSkewAsync(ct);
        if (index.Expires.Add(skew) < time.GetUtcNow())
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                $"index.json for '{repository.Name}' expired at {index.Expires:O} (D6; allowed clock skew {skew.TotalSeconds:0}s).");
        }

        await catalog.IngestAsync(repository, index, ct);
        await faults.CheckpointAsync(FaultCheckpoints.CatalogIngested, ct);

        return new IngestResult(repository.Name, index.Version, index.Packages.Count,
            index.Packages.Values.Sum(p => p.Versions.Count));
    }

    private async Task<TimeSpan> GetSkewAsync(CancellationToken ct)
    {
        var doc = await config.LoadAsync(ct);
        var seconds = doc.Values.TryGetValue("trust.maxClockSkewSeconds", out var v) && v is int i
            ? i
            : (int)Core.Config.ConfigDefinitions.Keys["trust.maxClockSkewSeconds"].Default;
        return TimeSpan.FromSeconds(seconds);
    }
}
