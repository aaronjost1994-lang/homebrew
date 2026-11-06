using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Trust;

namespace WinBrew.Application.Catalog;

public sealed record RepoAddRequest(string Name, string Url, IReadOnlyList<string> TrustRoots, bool NonInteractive);

public sealed record RepoAddResult(string Name, string Url, IReadOnlyList<string> RootKeyFingerprints, int RootVersion, int IndexVersion, int Packages);

/// <summary>Shows the root key fingerprints and asks whether to trust them (interactive sessions only).</summary>
public delegate Task<bool> TrustRootApproval(string repo, string url, IReadOnlyList<string> fingerprints, int threshold, CancellationToken ct);

/// <summary>
/// <c>wbrew repo add &lt;name&gt; &lt;url&gt; [--trust-root &lt;fingerprint&gt;...]</c> (D6
/// bootstrap). Downloads root.json, verifies it is signed by a threshold of
/// its own root keys, and requires approval of the root key fingerprints:
/// interactively, or non-interactively with <c>--trust-root</c> — every given
/// fingerprint must be a root key of that root and together they must reach
/// the root threshold. Without either, exit 10. No trust root is ever added
/// implicitly (<c>--yes</c> never implies it, A8). Then the normal verified
/// refresh runs and everything is committed in one transaction.
/// </summary>
public sealed class RepoAddUseCase(LifecycleServices s, MutationSession session, RepositoryMetadataService metadata)
{
    public async Task<RepoAddResult> ExecuteAsync(RepoAddRequest request, TrustRootApproval approve, CancellationToken ct)
    {
        if (!Identifiers.IsValidRepoName(request.Name))
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError,
                $"'{request.Name}' is not a valid repository name (^[a-z0-9](?:[a-z0-9-]{{0,30}}[a-z0-9])?$, A9).");
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, $"'{request.Url}' is not an absolute repository URL without query or credentials.");
        }

        var url = uri.ToString().TrimEnd('/');
        using var scope = await session.BeginAsync("repo add", ct);
        if (await s.Catalog.GetRepositoryAsync(request.Name, ct) is not null)
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"Repository '{request.Name}' is already configured; remove it first (wbrew repo remove {request.Name}).");
        }

        var root = await metadata.FetchBootstrapRootAsync(url, scope.Settings, ct);
        var fingerprints = root.RootFingerprints;
        var threshold = root.Metadata.Roles[TrustRoles.Root].Threshold;
        var details = new Dictionary<string, object?>
        {
            ["repository"] = request.Name,
            ["url"] = url,
            ["rootKeyFingerprints"] = fingerprints,
            ["threshold"] = threshold,
        };

        if (request.TrustRoots.Count > 0)
        {
            var given = request.TrustRoots.Select(f => f.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
            var unknown = given.Where(f => !fingerprints.Contains(f, StringComparer.Ordinal)).ToList();
            if (unknown.Count > 0)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"--trust-root {string.Join(", ", unknown)} does not match any root key of '{request.Name}' (D6). " +
                    $"The repository's root keys are: {string.Join(", ", fingerprints)}.", details);
            }

            if (given.Count < threshold)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"{given.Count} --trust-root fingerprint(s) given; the root threshold of '{request.Name}' is {threshold} (D6).", details);
            }
        }
        else if (request.NonInteractive)
        {
            throw new WinBrewException(WinBrewErrorCode.ConsentRequired,
                $"Adding '{request.Name}' needs approval of its root keys. Verify them out of band, then re-run with " +
                string.Join(" ", fingerprints.Select(f => "--trust-root " + f)) + " (D6, A8).", details);
        }
        else if (!await approve(request.Name, url, fingerprints, threshold, ct))
        {
            throw new OperationCanceledException("The repository's root keys were not approved.");
        }

        var outcome = await metadata.RefreshAsync(new RepositoryRecord(request.Name, url), root, state: null, isNew: true, scope.Settings, ct);
        var accepted = await s.Catalog.GetTrustStateAsync(request.Name, ct);
        return new RepoAddResult(request.Name, url, accepted?.RootFingerprints ?? fingerprints,
            outcome.RootVersion, outcome.IndexVersion, await s.Catalog.CountPackagesAsync(request.Name, ct));
    }
}

public sealed record RepoRow(
    string Name,
    string Url,
    IReadOnlyList<string> RootKeyFingerprints,
    int? RootVersion,
    int? TimestampVersion,
    int? IndexVersion,
    string? Expires,
    string? VerifiedAt,
    bool Expired,
    int Packages);

public sealed record RepoListResult(IReadOnlyList<RepoRow> Repositories);

/// <summary><c>wbrew repo list</c>: read-only, no lock (D6).</summary>
public sealed class RepoListUseCase(LifecycleServices s)
{
    public async Task<RepoListResult> ExecuteAsync(CancellationToken ct)
    {
        var settings = await LifecycleSettings.LoadAsync(s.Config, s.Flags, ct);
        var rows = new List<RepoRow>();
        foreach (var repository in await s.Catalog.ListRepositoriesAsync(ct))
        {
            var state = await s.Catalog.GetTrustStateAsync(repository.Name, ct);
            rows.Add(new RepoRow(repository.Name, repository.Url, state?.RootFingerprints ?? [],
                state?.RootVersion, state?.TimestampVersion, state?.IndexVersion,
                state is null ? null : Iso(state.Expires), state is null ? null : Iso(state.VerifiedAt),
                state is not null && TrustChain.IsExpired(state.Expires, s.Time.GetUtcNow(), settings.MaxClockSkew),
                await s.Catalog.CountPackagesAsync(repository.Name, ct)));
        }

        return new RepoListResult(rows);
    }

    internal static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record RepoRemoveResult(string Name, bool Removed);

/// <summary>
/// <c>wbrew repo remove &lt;name&gt;</c> (mutation lock, D6): removes the
/// repository, its trust state and its catalog. Refused (exit 6) while
/// packages from it are installed — they would lose their catalog and
/// their verified manifests' binding.
/// </summary>
public sealed class RepoRemoveUseCase(LifecycleServices s, MutationSession session)
{
    public async Task<RepoRemoveResult> ExecuteAsync(string name, CancellationToken ct)
    {
        using var scope = await session.BeginAsync("repo remove", ct);
        if (await s.Catalog.GetRepositoryAsync(name, ct) is null)
        {
            throw new WinBrewException(WinBrewErrorCode.NotFound, $"Repository '{name}' is not configured.");
        }

        var installed = (await s.Installed.ListInstalledAsync(ct)).Where(p => p.Repo == name).Select(p => Identifiers.Qualified(p.Repo, p.Id)).ToList();
        if (installed.Count > 0)
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"Packages from '{name}' are installed: {string.Join(", ", installed)}. Uninstall them before removing the repository.",
                new Dictionary<string, object?> { ["installed"] = installed });
        }

        var removed = await s.Catalog.RemoveRepositoryAsync(name, ct);
        var metadataDir = Path.Combine(s.Layout.Directories["cache.metadata"], name);
        if (s.Files.DirectoryExists(metadataDir))
        {
            await s.Files.DeleteDirectoryAsync(metadataDir, ct);
        }

        return new RepoRemoveResult(name, removed);
    }
}

public sealed record UpdateRow(string Repo, bool Ok, int? RootVersion, int? TimestampVersion, int? IndexVersion, bool IndexChanged, IReadOnlyList<int> RotatedRoots, string? Error);

public sealed record UpdateResult(IReadOnlyList<UpdateRow> Repositories);

/// <summary>
/// <c>wbrew update</c>: refreshes repository metadata only (never changes
/// installed versions), under the mutation lock (D6). Every repository is
/// tried; when any fails, the command fails with the first failure's code
/// after the others were refreshed.
/// </summary>
public sealed class UpdateUseCase(LifecycleServices s, MutationSession session, RepositoryMetadataService metadata)
{
    public async Task<UpdateResult> ExecuteAsync(bool offline, CancellationToken ct)
    {
        if (offline)
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, "'wbrew update' refreshes metadata from the network; it cannot run with --offline (A15).");
        }

        using var scope = await session.BeginAsync("update", ct);
        var rows = new List<UpdateRow>();
        WinBrewException? first = null;
        foreach (var repository in await s.Catalog.ListRepositoriesAsync(ct))
        {
            try
            {
                var state = await s.Catalog.GetTrustStateAsync(repository.Name, ct)
                    ?? throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                        $"Repository '{repository.Name}' has no accepted root; remove it and add it again with 'wbrew repo add' (D6).");
                var outcome = await metadata.RefreshAsync(repository, TrustChain.LoadAccepted(state.RootEnvelope), state, isNew: false, scope.Settings, ct);
                rows.Add(new UpdateRow(repository.Name, true, outcome.RootVersion, outcome.TimestampVersion, outcome.IndexVersion,
                    outcome.IndexChanged, outcome.RotatedRoots, null));
            }
            catch (WinBrewException ex)
            {
                first ??= ex;
                rows.Add(new UpdateRow(repository.Name, false, null, null, null, false, [], ex.Message));
            }
        }

        if (first is not null)
        {
            throw new WinBrewException(first.Code,
                "update failed: " + string.Join(" | ", rows.Where(r => !r.Ok).Select(r => $"{r.Repo}: {r.Error}")),
                new Dictionary<string, object?> { ["repositories"] = rows });
        }

        return new UpdateResult(rows);
    }
}

public sealed record SearchRow(string Package, string Repo, string Id, string DisplayName, string Description);

public sealed record SearchResult(string Query, IReadOnlyList<SearchRow> Results, [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

/// <summary>
/// <c>wbrew search &lt;query&gt;</c> (D6): prefix-or-substring match over
/// search_text built with ToLowerInvariant() at ingest; the query is
/// lowercased the same way; results ordered by id. Read-only: no lock, no
/// refresh; expired-but-once-valid metadata is shown with a warning.
/// </summary>
public sealed class SearchUseCase(LifecycleServices s, MetadataFreshness freshness)
{
    public async Task<SearchResult> ExecuteAsync(string query, CancellationToken ct)
    {
        var lowered = query.Trim().ToLowerInvariant();
        if (lowered.Length == 0)
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, "The search query is empty.");
        }

        if ((await s.Catalog.ListRepositoriesAsync(ct)).Count == 0)
        {
            throw new WinBrewException(WinBrewErrorCode.NotFound,
                "No repository is configured, so there is no verified catalog to search. Add one with 'wbrew repo add <name> <url>' (A8).");
        }

        var warnings = await freshness.ForReadOnlyAsync(ct);
        var hits = await s.Catalog.SearchAsync(lowered, ct);
        return new SearchResult(query, hits.Select(h => new SearchRow(Identifiers.Qualified(h.Repo, h.Id), h.Repo, h.Id, h.DisplayName, h.Description)).ToList(), warnings);
    }
}
