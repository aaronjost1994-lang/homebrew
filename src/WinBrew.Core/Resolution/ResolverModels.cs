using System.Runtime.InteropServices;
using WinBrew.Core.Artifacts;

namespace WinBrew.Core.Resolution;

/// <summary>A dependency exactly as written in the catalog (D6: verbatim id and range).</summary>
public sealed record CandidateDependency(string Id, string Range);

/// <summary>One catalog version entry: the resolver's only input per version (D2/D6).</summary>
public sealed record CandidateVersion(
    string Version,
    IReadOnlyList<string> Architectures,
    IReadOnlyList<CandidateDependency> Dependencies);

/// <summary>Every catalog version of one repository-qualified package.</summary>
public sealed record SnapshotPackage(string Repo, string Id, IReadOnlyList<CandidateVersion> Versions)
{
    public string Qualified => Identifiers.Qualified(Repo, Id);
}

/// <summary>
/// The in-memory catalog snapshot (D2): the resolver performs no I/O and
/// sees nothing else. <see cref="ConfiguredRepositories"/> decides whether a
/// fully qualified cross-repository dependency can resolve at all (A9).
/// </summary>
public sealed record ResolverSnapshot(
    IReadOnlyCollection<SnapshotPackage> Packages,
    IReadOnlySet<string> ConfiguredRepositories);

/// <summary>A top-level requirement: a requested package (with an optional exact version or range) or an installed one that must stay installed.</summary>
/// <param name="Qualified">&lt;repo&gt;/&lt;id&gt;.</param>
/// <param name="ExactVersion">Normalized version, or null.</param>
/// <param name="Range">NuGet interval (verbatim), or null for any version.</param>
/// <param name="Label">How explanations phrase it ("you requested …", "… is installed").</param>
public sealed record RootRequirement(string Qualified, string? ExactVersion, string? Range, string Label);

public enum ResolveMode
{
    /// <summary>D2 rule 2: prefer already-installed versions that satisfy all constraints.</summary>
    Install,

    /// <summary>D2 rule 3: the highest satisfying version for the upgrade targets.</summary>
    Upgrade,
}

/// <summary>Everything the resolver needs; built by the Application layer from SQLite before resolution.</summary>
/// <param name="Installed">Installed packages: qualified id → normalized version.</param>
/// <param name="Pins">Pinned packages: qualified id → the installed version they are fixed at (D2 rule 1).</param>
/// <param name="UpgradeTargets">Upgrade mode: packages that should move to their highest version (empty = every unpinned package).</param>
public sealed record ResolverRequest(
    ResolverSnapshot Snapshot,
    Architecture Host,
    IReadOnlyList<RootRequirement> Roots,
    IReadOnlyDictionary<string, string> Installed,
    IReadOnlyDictionary<string, string> Pins,
    ResolveMode Mode,
    IReadOnlySet<string> UpgradeTargets);

/// <summary>One selected package with its A10 artifact architecture and its qualified dependencies.</summary>
public sealed record ResolvedPackage(
    string Repo,
    string Id,
    string Version,
    ArchitectureSelection Architecture,
    IReadOnlyList<string> Dependencies)
{
    public string Qualified => Identifiers.Qualified(Repo, Id);
}

/// <summary>Why resolution failed, for people (lines) and for --json (structured).</summary>
public sealed record ConflictReport(
    IReadOnlyList<string> Explanation,
    IReadOnlyList<ConflictCause> Causes,
    string? MissingRepository);

/// <summary>An external fact the conflict was derived from (D2: repository-qualified ids, original range strings).</summary>
public sealed record ConflictCause(string Kind, string Package, string? Version, string? Dependency, string? Range, string Text);

/// <summary>The resolver's outcome. On success, <see cref="Packages"/> is in install order (dependencies first).</summary>
public sealed record ResolverResult(IReadOnlyList<ResolvedPackage> Packages, ConflictReport? Conflict)
{
    public bool Success => Conflict is null;
}
