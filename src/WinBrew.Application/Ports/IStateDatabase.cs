namespace WinBrew.Application.Ports;

/// <summary>
/// SQLite state database port (A6): WAL mode, forward-only user_version
/// migrations with a backup before every migration, never downgrade.
/// </summary>
public interface IStateDatabase
{
    /// <summary>Latest schema version this binary understands.</summary>
    int MaxSupportedSchemaVersion { get; }

    /// <summary>The database's current user_version; -1 when no database file exists yet.</summary>
    Task<int> GetCurrentSchemaVersionAsync(CancellationToken ct);

    /// <summary>
    /// Applies pending forward-only migrations. Copies the database into
    /// state\backups\ (SQLite backup API) before each migration step.
    /// A database with a NEWER schema than this binary supports produces a
    /// clear WinBrewException (mapped to exit 1, A6). Never downgrades.
    /// </summary>
    Task ApplyMigrationsAsync(CancellationToken ct);

    /// <summary>
    /// The FTS5 probe (Phase A): true when the bundled SQLitePCLRaw build
    /// includes FTS5. The result decides the D6 search implementation and is
    /// recorded in the Phase A checkpoint/ADR.
    /// </summary>
    Task<bool> SupportsFts5Async(CancellationToken ct);

    /// <summary>
    /// self uninstall (A14): releases pooled connections, then deletes
    /// winbrew.db (+ -wal/-shm) and the migration backups. The lock file is left
    /// alone (it is held by the running command).
    /// </summary>
    Task DeleteDatabaseAsync(CancellationToken ct);
}
