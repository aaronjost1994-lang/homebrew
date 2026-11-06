using System.Globalization;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Catalog;
using WinBrew.Core.Operations;
using WinBrew.Core.Trust;

namespace WinBrew.Application.Catalog;

/// <summary>What one verified refresh accepted.</summary>
public sealed record RefreshOutcome(
    string Repo,
    int RootVersion,
    IReadOnlyList<int> RotatedRoots,
    int TimestampVersion,
    int IndexVersion,
    bool IndexChanged,
    DateTimeOffset Expires);

/// <summary>
/// The ONE verified metadata path (D6), used by <c>repo add</c>, <c>update</c>
/// and the automatic refresh of expired metadata (A8):
/// root (walk root/N+1.json sequentially) → timestamp → index (length and
/// SHA-256 from timestamp.json, enforced while streaming; trust.maxMetadataBytes)
/// → ONE SQLite transaction with the catalog rows and every accepted role
/// version. Callers hold the mutation lock.
/// </summary>
public sealed class RepositoryMetadataService(LifecycleServices s)
{
    /// <summary>Upper bound on rotations walked in one refresh (a hostile server cannot loop us).</summary>
    public const int MaxRootRotations = 1024;

    /// <summary>repo add: downloads root.json and verifies it is signed by a threshold of its own keys.</summary>
    public async Task<TrustedRoot> FetchBootstrapRootAsync(string url, LifecycleSettings settings, CancellationToken ct)
    {
        var bytes = await GetAsync(url, "root.json", null, settings, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"{url} has no root.json; is this a WinBrew repository? (D6)");
        return TrustChain.Bootstrap(bytes, s.Time.GetUtcNow(), settings.MaxClockSkew);
    }

    public async Task<RefreshOutcome> RefreshAsync(
        RepositoryRecord repository, TrustedRoot start, RepositoryTrustState? state, bool isNew, LifecycleSettings settings, CancellationToken ct)
    {
        var now = s.Time.GetUtcNow();
        var skew = settings.MaxClockSkew;

        var (root, rotated) = await WalkRootsAsync(repository.Url, start, settings, ct);
        TrustChain.EnsureCurrent(root, now, skew);

        var timestampBytes = await GetAsync(repository.Url, "timestamp.json", null, settings, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"Repository '{repository.Name}' has no timestamp.json (D6).");
        var timestamp = TrustChain.VerifyTimestamp(root, timestampBytes, state?.TimestampVersion, now, skew);

        RepositoryIndex? index = null;
        int indexVersion;
        DateTimeOffset indexExpires;
        if (state is not null && timestamp.Version == state.TimestampVersion)
        {
            // Nothing new was published: the accepted index stays current — if it is still valid.
            indexVersion = state.IndexVersion;
            indexExpires = state.IndexExpires;
            if (TrustChain.IsExpired(indexExpires, now, skew))
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"index.json for '{repository.Name}' expired at {indexExpires:O} and the repository published no newer timestamp (D6).");
            }
        }
        else
        {
            var meta = timestamp.Index;
            if (meta.Length > settings.MaxMetadataBytes)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"timestamp.json declares an index.json of {meta.Length} bytes, above trust.maxMetadataBytes ({settings.MaxMetadataBytes}, D6).");
            }

            var indexBytes = await GetAsync(repository.Url, TrustRoles.IndexFile, meta.Length, settings, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"Repository '{repository.Name}' has no index.json (D6).");
            var payload = TrustChain.VerifyIndexEnvelope(root, indexBytes, meta, TrustRoles.IndexPayloadType);
            index = RepositoryIndexParser.Parse(payload);
            TrustChain.EnsureIndexFresh(index.Version, index.Expires, state?.IndexVersion, now, skew);
            indexVersion = index.Version;
            indexExpires = index.Expires;
        }

        var commit = new RepositoryMetadataCommit(repository, root, timestamp, index, indexVersion, indexExpires, now, isNew);
        await s.Catalog.CommitRepositoryMetadataAsync(commit,
            async token => await s.Faults.CheckpointAsync(FaultCheckpoints.CatalogRowsWritten, token), ct);
        await s.Faults.CheckpointAsync(FaultCheckpoints.CatalogIngested, ct);

        return new RefreshOutcome(repository.Name, root.Version, rotated, timestamp.Version, indexVersion, index is not null,
            timestamp.Expires < indexExpires ? timestamp.Expires : indexExpires);
    }

    /// <summary>Walks root/&lt;N+1&gt;.json until the server has no newer root (HTTP 404).</summary>
    public async Task<(TrustedRoot Root, IReadOnlyList<int> Rotated)> WalkRootsAsync(string url, TrustedRoot start, LifecycleSettings settings, CancellationToken ct)
    {
        var root = start;
        var rotated = new List<int>();
        for (var i = 0; i < MaxRootRotations; i++)
        {
            var next = await GetAsync(url, $"root/{root.Version + 1}.json", null, settings, ct);
            if (next is null)
            {
                return (root, rotated);
            }

            root = TrustChain.Rotate(root, next);
            rotated.Add(root.Version);
        }

        throw new WinBrewException(WinBrewErrorCode.VerificationFailure, $"More than {MaxRootRotations} root rotations offered by {url} (D6).");
    }

    /// <summary>Fetches &lt;url&gt;/&lt;path&gt; under trust.maxMetadataBytes; null on HTTP 404.</summary>
    public async Task<byte[]?> GetAsync(string url, string path, long? expectedSize, LifecycleSettings settings, CancellationToken ct)
    {
        var uri = new Uri(new Uri(url.TrimEnd('/') + "/"), path);
        try
        {
            var (body, _) = await s.Http.GetBytesAsync(new FetchRequest(uri, expectedSize, settings.MaxMetadataBytes,
                settings.DownloadTimeout, settings.DownloadRetries, settings.Localhost), ct);
            return body;
        }
        catch (WinBrewException ex) when (ex.Code == WinBrewErrorCode.NetworkFailure &&
                                          ex.Details.TryGetValue("status", out var status) && status is 404)
        {
            return null;
        }
    }
}

/// <summary>
/// A8/A15 freshness rules for the verified catalog. Read-only commands show
/// expired-but-once-valid metadata with a warning and never refresh;
/// --dry-run refuses expired metadata (exit 5, advice); online mutating
/// commands refresh expired metadata through the verified path and say so;
/// --offline uses it with a warning that includes its age. Metadata that was
/// never verified is never used (exit 3).
/// </summary>
public sealed class MetadataFreshness(LifecycleServices s, RepositoryMetadataService refresher)
{
    public async Task<IReadOnlyList<string>> ForReadOnlyAsync(CancellationToken ct)
    {
        var settings = await LifecycleSettings.LoadAsync(s.Config, s.Flags, ct);
        var warnings = new List<string>();
        foreach (var state in await StatesAsync(ct))
        {
            if (IsExpired(state, settings))
            {
                warnings.Add($"Repository '{state.Repo}' metadata expired {Age(state.Expires)} ago (last verified {Age(state.VerifiedAt)} ago); " +
                             "showing it anyway. Run 'wbrew update' to refresh (A8).");
            }
        }

        return warnings;
    }

    public async Task EnsureForDryRunAsync(CancellationToken ct)
    {
        var settings = await LifecycleSettings.LoadAsync(s.Config, s.Flags, ct);
        foreach (var state in await StatesAsync(ct))
        {
            if (IsExpired(state, settings))
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Repository '{state.Repo}' metadata expired {Age(state.Expires)} ago; --dry-run never refreshes it. Run 'wbrew update' first (A8).");
            }
        }
    }

    /// <summary>Inside a mutation scope: refresh (online) or warn (--offline) for every expired repository.</summary>
    public async Task<IReadOnlyList<string>> EnsureForMutationAsync(MutationScope scope, bool offline, CancellationToken ct)
    {
        var messages = new List<string>();
        foreach (var state in await StatesAsync(ct))
        {
            if (!IsExpired(state, scope.Settings))
            {
                continue;
            }

            if (offline)
            {
                messages.Add($"--offline: using repository '{state.Repo}' metadata that expired {Age(state.Expires)} ago " +
                             $"(it was fully verified {Age(state.VerifiedAt)} ago while valid, A15).");
                continue;
            }

            var root = TrustChain.LoadAccepted(state.RootEnvelope);
            await refresher.RefreshAsync(new RepositoryRecord(state.Repo, state.Url), root, state, isNew: false, scope.Settings, ct);
            messages.Add($"Repository metadata refreshed: '{state.Repo}' timestamp expired {Age(state.Expires)} ago (A8).");
        }

        return messages;
    }

    private async Task<IReadOnlyList<RepositoryTrustState>> StatesAsync(CancellationToken ct)
    {
        // No repository configured: nothing to check (lookups then fail with exit 3 on their own).
        var repositories = await s.Catalog.ListRepositoriesAsync(ct);
        var states = new List<RepositoryTrustState>();
        foreach (var repository in repositories)
        {
            states.Add(await s.Catalog.GetTrustStateAsync(repository.Name, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"No metadata was ever verified for repository '{repository.Name}'. Remove it and add it again with 'wbrew repo add' (D6)."));
        }

        return states;
    }

    private bool IsExpired(RepositoryTrustState state, LifecycleSettings settings) =>
        TrustChain.IsExpired(state.Expires, s.Time.GetUtcNow(), settings.MaxClockSkew);

    private string Age(DateTimeOffset since) => FormatAge(s.Time.GetUtcNow() - since);

    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalDays >= 1 ? Plural((int)age.TotalDays, "day")
            : age.TotalHours >= 1 ? Plural((int)age.TotalHours, "hour")
            : Plural(Math.Max(1, (int)age.TotalMinutes), "minute");
    }

    private static string Plural(int n, string unit) => string.Create(CultureInfo.InvariantCulture, $"{n} {unit}{(n == 1 ? "" : "s")}");
}
