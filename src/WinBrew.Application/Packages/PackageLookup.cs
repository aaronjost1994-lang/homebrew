using System.Runtime.InteropServices;
using NuGet.Versioning;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Artifacts;

namespace WinBrew.Application.Packages;

/// <summary>A parsed <c>&lt;package&gt;[@&lt;version&gt;]</c> request (A8).</summary>
public sealed record PackageRequest(string? Repo, string Id, string? Version);

/// <summary>Catalog lookups shared by install, uninstall and info (A9, D6).</summary>
public static class PackageLookup
{
    /// <summary>
    /// Parses <c>&lt;package&gt;[@&lt;version&gt;]</c> plus an optional
    /// <c>--version</c>. Both at once is a usage error (exit 2, A8).
    /// </summary>
    public static PackageRequest Parse(string package, string? versionOption)
    {
        var at = package.IndexOf('@', StringComparison.Ordinal);
        var reference = at >= 0 ? package[..at] : package;
        var inlineVersion = at >= 0 ? package[(at + 1)..] : null;

        if (inlineVersion is not null && versionOption is not null)
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError,
                "Specify the version either as <package>@<version> or with --version, not both (A8).");
        }

        if (!Identifiers.TrySplitReference(reference, out var repo, out var id))
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError,
                $"'{reference}' is not a valid package reference ('<id>' or '<repo>/<id>', A9).");
        }

        var version = inlineVersion ?? versionOption;
        if (version is not null)
        {
            if (version.Length == 0)
            {
                throw new WinBrewException(WinBrewErrorCode.UsageError, "Empty version after '@'.");
            }

            version = VersionOps.NormalizeVersion(version);
        }

        return new PackageRequest(repo, id, version);
    }

    /// <summary>
    /// Resolves the repository of a package id. An unqualified id found in
    /// more than one repository is an error listing the qualified
    /// candidates (exit 2); there is no implicit repository priority (A9).
    /// </summary>
    public static async Task<CatalogPackage> FindAsync(ICatalogStore catalog, PackageRequest request, CancellationToken ct)
    {
        string repo;
        if (request.Repo is not null)
        {
            if (await catalog.GetRepositoryAsync(request.Repo, ct) is null)
            {
                throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"Repository '{request.Repo}' is not configured.");
            }

            repo = request.Repo;
        }
        else
        {
            var repos = await catalog.FindRepositoriesForAsync(request.Id, ct);
            if (repos.Count == 0)
            {
                var configured = await catalog.ListRepositoriesAsync(ct);
                throw new WinBrewException(WinBrewErrorCode.NotFound,
                    configured.Count == 0
                        ? $"Package '{request.Id}' not found: no repository metadata has been ingested yet."
                        : $"Package '{request.Id}' was not found in any configured repository.");
            }

            if (repos.Count > 1)
            {
                var candidates = repos.Select(r => Identifiers.Qualified(r, request.Id)).ToList();
                throw new WinBrewException(WinBrewErrorCode.UsageError,
                    $"'{request.Id}' is ambiguous; use a qualified id: {string.Join(", ", candidates)} (A9).",
                    new Dictionary<string, object?> { ["candidates"] = candidates });
            }

            repo = repos[0];
        }

        return await catalog.GetPackageAsync(repo, request.Id, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound,
                $"Package '{Identifiers.Qualified(repo, request.Id)}' was not found in the catalog.");
    }

    /// <summary>
    /// Picks the version to install: the requested one, else the highest
    /// version with an artifact for the host; prereleases only when the
    /// requested version is a prerelease (D2 rule 6). The artifact
    /// architecture follows the A10 order.
    /// </summary>
    public static (CatalogVersion Version, ArchitectureSelection Arch) SelectVersion(
        CatalogPackage package, string? requestedVersion, Architecture host)
    {
        var qualified = Identifiers.Qualified(package.Repo, package.Id);

        if (requestedVersion is not null)
        {
            var exact = package.Versions.FirstOrDefault(v => string.Equals(v.Version, requestedVersion, StringComparison.Ordinal))
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"Version {requestedVersion} of {qualified} is not in the catalog.");

            var selection = HostArchitectureRules.Select(host, exact.Architectures)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"{qualified}@{exact.Version} has no artifact for this {HostArchitectureRules.HostName(host)} host " +
                    $"(offered: {string.Join(", ", exact.Architectures)}; accepted order: " +
                    $"{string.Join(" → ", HostArchitectureRules.SelectionOrder(host))}, A10).");
            return (exact, selection);
        }

        var candidates = package.Versions
            .Where(v => !v.IsPrerelease)
            .Select(v => (Version: v, Parsed: NuGetVersion.Parse(v.Version), Arch: HostArchitectureRules.Select(host, v.Architectures)))
            .Where(c => c.Arch is not null)
            .OrderByDescending(c => c.Parsed)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new WinBrewException(WinBrewErrorCode.NotFound,
                $"{qualified} has no stable version with an artifact for this {HostArchitectureRules.HostName(host)} host " +
                "(prereleases are selected only with an explicit prerelease --version, D2).");
        }

        return (candidates[0].Version, candidates[0].Arch!);
    }
}
