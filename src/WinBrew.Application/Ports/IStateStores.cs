using WinBrew.Core.Catalog;
using WinBrew.Core.Operations;

namespace WinBrew.Application.Ports;

// ---- catalog (D6) ----------------------------------------------------------

/// <summary>A configured repository row.</summary>
public sealed record RepositoryRecord(string Name, string Url);

/// <summary>One catalog version entry (manifest digest/size, architectures, dependencies verbatim).</summary>
public sealed record CatalogVersion(
    string Version,
    string ManifestSha256,
    long ManifestSize,
    bool IsPrerelease,
    IReadOnlyList<string> Architectures,
    IReadOnlyList<IndexDependency> Dependencies);

/// <summary>A catalog package with every version entry.</summary>
public sealed record CatalogPackage(
    string Repo,
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<CatalogVersion> Versions);

/// <summary>
/// The SQLite catalog (D6): the ONLY input for planning. Ingest writes the
/// catalog rows and the accepted index version (high-water mark) in ONE
/// transaction; a lower version than the accepted one is a rollback attack
/// and is rejected (VerificationFailure, exit 5).
/// </summary>
public interface ICatalogStore
{
    Task IngestAsync(RepositoryRecord repository, RepositoryIndex index, CancellationToken ct);

    Task<RepositoryRecord?> GetRepositoryAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesAsync(CancellationToken ct);

    /// <summary>Repositories whose catalog contains <paramref name="id"/> (ambiguity check, A9).</summary>
    Task<IReadOnlyList<string>> FindRepositoriesForAsync(string id, CancellationToken ct);

    Task<CatalogPackage?> GetPackageAsync(string repo, string id, CancellationToken ct);

    /// <summary>The accepted high-water mark of a role (D6), or null.</summary>
    Task<int?> GetAcceptedVersionAsync(string repo, string role, CancellationToken ct);
}

/// <summary>
/// Signature verification of repository metadata (D6). Phase B ingests an
/// UNSIGNED index.json through the final ingest path; the test-hooks build
/// registers TestHooksUnsignedVerifier (compiled out of release builds) and
/// release builds refuse all metadata until Phase D brings real signing.
/// </summary>
public interface IMetadataVerifier
{
    /// <summary>Returns the verified index.json payload bytes, or throws VerificationFailure (exit 5).</summary>
    Task<byte[]> VerifyIndexAsync(string repo, byte[] document, CancellationToken ct);
}

// ---- installed state (A9/A10, D4) ---------------------------------------------

public sealed record InstalledPackage(
    string Repo,
    string Id,
    string Version,
    string Arch,
    bool Explicit,
    string InstalledAt,
    string UpdatedAt);

/// <summary>A managed shim row: ownership lives in the database, never in a hash (D4).</summary>
public sealed record ShimRecord(
    string Name,
    string OwnerRepo,
    string OwnerPackage,
    string OwnerVersion,
    string TargetPath,
    string Subsystem,
    string TemplateSha256);

/// <summary>The installed-state half of a StateCommitted transaction for one package.</summary>
/// <param name="Dependencies">The package's resolved dependencies as qualified ids: they REPLACE its dependency edges (A10).</param>
public sealed record InstallCommit(
    InstalledPackage Package,
    IReadOnlyList<ShimRecord> Shims,
    IReadOnlyList<string> RemovedShimNames,
    IReadOnlyList<string> Dependencies);

/// <summary>One stored dependency edge (A10: every edge is recorded).</summary>
public sealed record DependencyEdge(string Dependent, string Dependency);

public interface IInstalledStore
{
    Task<InstalledPackage?> GetInstalledAsync(string repo, string id, CancellationToken ct);

    Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct);

    Task<ShimRecord?> GetShimAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<ShimRecord>> GetShimsForAsync(string repo, string id, CancellationToken ct);

    /// <summary>Installed packages that depend on the target (D8).</summary>
    Task<IReadOnlyList<string>> GetDependentsAsync(string repo, string id, CancellationToken ct);

    /// <summary>
    /// ONE SQLite transaction for a whole plan: installed rows, shim rows (+
    /// removals), dependency edges, and the operation's state = StateCommitted
    /// (A11: files and SQLite are separate resources; this covers only the
    /// SQLite side).
    /// </summary>
    Task CommitPlanAsync(string opId, IReadOnlyList<InstallCommit> commits, CancellationToken ct);

    Task<IReadOnlyList<DependencyEdge>> ListDependencyEdgesAsync(CancellationToken ct);

    /// <summary>Pinned packages: qualified id → the installed version they are fixed at (D2 rule 1).</summary>
    Task<IReadOnlyDictionary<string, string>> GetPinsAsync(CancellationToken ct);

    /// <summary>Pins or unpins an installed package. Returns false when nothing changed.</summary>
    Task<bool> SetPinnedAsync(string repo, string id, bool pinned, CancellationToken ct);

    /// <summary>ONE transaction: delete the installed row (cascading shims/edges/pins) + state = StateCommitted.</summary>
    Task CommitUninstallAsync(string opId, string repo, string id, CancellationToken ct);
}

// ---- journal (A11) -------------------------------------------------------------

public sealed record OperationRecord(
    string OpId,
    OperationType Type,
    OperationState State,
    string Command,
    string? Error,
    string StartedAt,
    string UpdatedAt);

public sealed record OperationStep(
    int Seq,
    string Name,
    string IntentJson,
    string? OutcomeJson,
    string State);

public sealed record JournalFile(string OpId, int Seq, string Path, string Kind);

/// <summary>
/// The operation journal (A11): one operations row plus ordered steps with
/// intent written BEFORE and outcome written AFTER each filesystem action,
/// and every durable file side-effect (journal_files) — including every
/// .old.&lt;guid&gt; rename (D4).
/// </summary>
public interface IOperationJournal
{
    Task CreateAsync(string opId, OperationType type, string command, CancellationToken ct);

    /// <summary>Writes a step's intent; returns its sequence number.</summary>
    Task<int> BeginStepAsync(string opId, string name, string intentJson, OperationState state, CancellationToken ct);

    Task CompleteStepAsync(string opId, int seq, string outcomeJson, CancellationToken ct);

    Task SetStateAsync(string opId, OperationState state, string? failureReport, CancellationToken ct);

    /// <summary>Records a durable file side-effect (kind: old_shim | old_dir | staging | sidecar).</summary>
    Task RecordFileAsync(string opId, int seq, string path, string kind, CancellationToken ct);

    Task MarkFileDeletedAsync(JournalFile file, CancellationToken ct);

    /// <summary>Recorded files not yet deleted whose operation reached a terminal state (D4/PLAN §1 cleanup rule).</summary>
    Task<IReadOnlyList<JournalFile>> GetPendingCleanupAsync(CancellationToken ct);

    Task<IReadOnlyList<OperationRecord>> GetIncompleteAsync(CancellationToken ct);

    Task<IReadOnlyList<OperationRecord>> GetRecoveryFailedAsync(CancellationToken ct);

    Task<OperationRecord?> GetAsync(string opId, CancellationToken ct);

    Task<IReadOnlyList<OperationStep>> GetStepsAsync(string opId, CancellationToken ct);
}
