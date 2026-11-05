namespace WinBrew.Core.Operations;

/// <summary>
/// Registry of fault-injection checkpoint names (A11). Names are static and
/// enumerable so the test suite can enumerate them and never hardcode a
/// count. The registry grows as phases implement durable transitions;
/// additions are backward-compatible (PLAN §6).
///
/// Phase B registers every durable transition of the portable lifecycle,
/// uninstall, self install, PATH and catalog ingest. Names that belong to
/// later phases (trust.root.accepted, selfupdate.app.copied — Phase D) are
/// registered only when a phase actually fires them, so "crash at every
/// registered checkpoint" stays executable in every phase.
/// Per-shim checkpoints fire once per shim (occurrence syntax
/// WINBREW_FAULT_AT=&lt;name&gt;#N, PLAN §1).
/// </summary>
public static class FaultCheckpoints
{
    // ---- portable install / upgrade (A11 steps 3–9, PLAN §6) ------------
    public const string DownloadStarted = "download.started";
    public const string DownloadCompleted = "download.completed";
    public const string ArtifactVerified = "artifact.verified";
    public const string ArtifactCached = "artifact.cached";
    public const string ExtractCompleted = "extract.completed";
    public const string ValidateCompleted = "validate.completed";
    public const string PackagePlaced = "package.placed";
    public const string ShimSidecarReplaced = "shim.sidecar.replaced";
    public const string ShimBinaryReplaced = "shim.binary.replaced";
    public const string PackageActivated = "package.activated";
    public const string StateCommitted = "state.committed";
    public const string OperationCompleted = "operation.completed";

    // ---- compensation / recovery (A11) ----------------------------------
    public const string CompensateStarted = "compensate.started";
    public const string CompensateShimReverted = "compensate.shim.reverted";
    public const string CompensatePackageRemoved = "compensate.package.removed";
    public const string RollbackCompleted = "rollback.completed";
    public const string RecoveryFailed = "recovery.failed";

    // ---- uninstall (Phase B addition; roll-forward after commit) --------
    public const string UninstallShimRemoved = "uninstall.shim.removed";
    public const string UninstallPackageRemoved = "uninstall.package.removed";

    // ---- PATH, catalog, self install -------------------------------------
    public const string PathUpdated = "path.updated";
    public const string CatalogIngested = "catalog.ingested";
    public const string SelfInstallAppCopied = "selfinstall.app.copied";

    /// <summary>All registered checkpoint names, sorted. Tests enumerate this list.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        DownloadStarted, DownloadCompleted, ArtifactVerified, ArtifactCached,
        ExtractCompleted, ValidateCompleted, PackagePlaced, ShimSidecarReplaced,
        ShimBinaryReplaced, PackageActivated, StateCommitted, OperationCompleted,
        CompensateStarted, CompensateShimReverted, CompensatePackageRemoved,
        RollbackCompleted, RecoveryFailed, UninstallShimRemoved,
        UninstallPackageRemoved, PathUpdated, CatalogIngested, SelfInstallAppCopied,
    }.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Throws when a phase tries to fire an unregistered name — keeps the registry authoritative.</summary>
    public static void EnsureRegistered(string name)
    {
        if (!All.Contains(name, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fault checkpoint '{name}' is not registered in {nameof(FaultCheckpoints)}. " +
                "Register durable-transition names here so tests can enumerate them (A11).");
        }
    }
}
