using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinBrew.Core.Bundles;

public sealed record BundleRepository(string Name, string Url);

public sealed record BundlePackage(string Id, string? Range = null, bool Pinned = false);

/// <summary>bundle.v1 (A15): requested packages with optional ranges and pins, plus repository names and URLs.</summary>
public sealed record Bundle(string SchemaVersion, IReadOnlyList<BundleRepository> Repositories, IReadOnlyList<BundlePackage> Packages);

public sealed record LockRepository(string Name, string Url, IReadOnlyList<string> RootKeyFingerprints);

public sealed record LockPackage(
    string Repo,
    string Id,
    string Version,
    string Architecture,
    string ManifestSha256,
    string ArtifactUrl,
    string ArtifactSha256,
    bool Explicit);

/// <summary>lockfile.v1 (A15): the exact selection; <c>bundle install --locked</c> never re-resolves.</summary>
public sealed record Lockfile(
    string SchemaVersion,
    IReadOnlyList<LockRepository> Repositories,
    IReadOnlyList<LockPackage> Packages,
    string? WinbrewVersion = null);

/// <summary>
/// Strict bundle/lockfile parsing (unknown members rejected) plus the rules
/// beyond the JSON shape. Problems are UsageError (exit 2): these are user
/// input files. URLs are checked by the HTTP policy (A13) when used.
/// </summary>
public static partial class BundleFiles
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    private static readonly JsonSerializerOptions WriteOptions = new(BundleJsonContext.Default.Options) { WriteIndented = true };

    public static Bundle ParseBundle(ReadOnlySpan<byte> json, string path)
    {
        var bundle = Deserialize(json, BundleJsonContext.Default.Bundle, path);
        RequireSchema(bundle.SchemaVersion, path);
        CheckRepositories(bundle.Repositories.Select(r => r.Name), path);
        foreach (var package in bundle.Packages)
        {
            if (!Identifiers.TrySplitReference(package.Id, out _, out _))
            {
                throw Invalid(path, $"'{package.Id}' is not a valid package id or <repo>/<id>");
            }

            if (package.Range is not null)
            {
                try
                {
                    VersionOps.ValidateRange(package.Range, $"{path}: {package.Id}");
                }
                catch (WinBrewException ex)
                {
                    throw Invalid(path, ex.Message);
                }
            }
        }

        if (bundle.Packages.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != bundle.Packages.Count)
        {
            throw Invalid(path, "a package is listed more than once");
        }

        return bundle;
    }

    public static Lockfile ParseLockfile(ReadOnlySpan<byte> json, string path)
    {
        var lockfile = Deserialize(json, BundleJsonContext.Default.Lockfile, path);
        RequireSchema(lockfile.SchemaVersion, path);
        CheckRepositories(lockfile.Repositories.Select(r => r.Name), path);
        var repos = lockfile.Repositories.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var repo in lockfile.Repositories)
        {
            if (repo.RootKeyFingerprints.Count == 0 || repo.RootKeyFingerprints.Any(f => !Sha256Regex().IsMatch(f)))
            {
                throw Invalid(path, $"repository '{repo.Name}' needs root key fingerprints (64 lowercase hex)");
            }
        }

        foreach (var p in lockfile.Packages)
        {
            var where = $"{p.Repo}/{p.Id}@{p.Version}";
            if (!repos.Contains(p.Repo))
            {
                throw Invalid(path, $"{where} names repository '{p.Repo}', which the lockfile does not list");
            }

            if (!Identifiers.IsValidPackageId(p.Id) || !VersionOps.TryNormalizeVersion(p.Version, out var normalized) || normalized != p.Version)
            {
                throw Invalid(path, $"{where} has an invalid id or a non-normalized version");
            }

            if (p.Architecture is not ("x64" or "arm64" or "x86") || !Sha256Regex().IsMatch(p.ManifestSha256) ||
                !Sha256Regex().IsMatch(p.ArtifactSha256) || !Uri.TryCreate(p.ArtifactUrl, UriKind.Absolute, out _))
            {
                throw Invalid(path, $"{where} has an invalid architecture, digest or artifact URL");
            }
        }

        if (lockfile.Packages.Select(p => (p.Repo, p.Id)).Distinct().Count() != lockfile.Packages.Count)
        {
            throw Invalid(path, "a package is locked more than once");
        }

        return lockfile;
    }

    public static byte[] Serialize(Bundle bundle) => JsonSerializer.SerializeToUtf8Bytes(bundle, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Bundle>)WriteOptions.GetTypeInfo(typeof(Bundle)));

    public static byte[] Serialize(Lockfile lockfile) => JsonSerializer.SerializeToUtf8Bytes(lockfile, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Lockfile>)WriteOptions.GetTypeInfo(typeof(Lockfile)));

    /// <summary>A15: <c>&lt;bundle&gt;.lock.json</c> beside the bundle (dev.json → dev.lock.json).</summary>
    public static string DefaultLockPath(string bundlePath)
    {
        var full = Path.GetFullPath(bundlePath);
        var name = Path.GetFileName(full);
        var stem = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        return Path.Combine(Path.GetDirectoryName(full)!, stem + ".lock.json");
    }

    private static void RequireSchema(string version, string path)
    {
        if (version != "1")
        {
            throw Invalid(path, $"schemaVersion '{version}' is not supported (expected \"1\")");
        }
    }

    private static void CheckRepositories(IEnumerable<string> names, string path)
    {
        var list = names.ToList();
        var bad = list.FirstOrDefault(n => !Identifiers.IsValidRepoName(n));
        if (bad is not null)
        {
            throw Invalid(path, $"'{bad}' is not a valid repository name (A9)");
        }

        if (list.Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            throw Invalid(path, "a repository is listed more than once");
        }
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string path)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type) ?? throw Invalid(path, "the document is empty");
        }
        catch (JsonException ex)
        {
            throw Invalid(path, ex.Message);
        }
    }

    private static WinBrewException Invalid(string path, string reason) =>
        new(WinBrewErrorCode.UsageError, $"{path}: {reason} (A15).");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(Bundle))]
[JsonSerializable(typeof(Lockfile))]
internal sealed partial class BundleJsonContext : JsonSerializerContext
{
}
