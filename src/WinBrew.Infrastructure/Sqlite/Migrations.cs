namespace WinBrew.Infrastructure.Sqlite;

/// <summary>One forward-only migration step (A6).</summary>
public sealed record Migration(int Version, string Description, string Ddl);

/// <summary>
/// The migration list (A6): forward-only, applied in order, each preceded by
/// a backup copy. Never downgrade; a newer database than the binary supports
/// is a clear error.
/// </summary>
public static class Migrations
{
    public const int LatestVersion = 1;

    public static readonly IReadOnlyList<Migration> All =
    [
        new(1, "initial schema v1", """
            -- repositories (one row per configured repo)
            CREATE TABLE repositories (
              name       TEXT PRIMARY KEY,          -- short name; used in paths (A6)
              url        TEXT NOT NULL,
              created_at TEXT NOT NULL              -- ISO-8601, UTC
            );

            -- per-role replay protection (D6): reject any version below the mark
            CREATE TABLE role_versions (
              repo       TEXT NOT NULL REFERENCES repositories(name) ON DELETE CASCADE,
              role       TEXT NOT NULL CHECK (role IN ('root','timestamp','index')),
              accepted_version INTEGER NOT NULL,
              accepted_at TEXT NOT NULL,
              PRIMARY KEY (repo, role)
            );

            -- catalog: package summaries (D6 sparse index, resolver's only input)
            CREATE TABLE catalog_packages (
              repo        TEXT NOT NULL REFERENCES repositories(name) ON DELETE CASCADE,
              id          TEXT NOT NULL,
              display_name TEXT NOT NULL,
              description TEXT NOT NULL,
              search_text TEXT NOT NULL,            -- ToLowerInvariant(id+name+desc), D6
              PRIMARY KEY (repo, id)
            );
            CREATE INDEX idx_pkg_search ON catalog_packages(search_text);

            -- catalog: version entries (sha256/size = the MANIFEST digest+size
            -- from index.json; artifact digests live in manifests, A10/A11)
            CREATE TABLE catalog_versions (
              repo        TEXT NOT NULL REFERENCES repositories(name) ON DELETE CASCADE,
              package_id  TEXT NOT NULL,
              version     TEXT NOT NULL,            -- NuGetVersion ToNormalizedString() (D3)
              manifest_sha256 TEXT NOT NULL,
              manifest_size   INTEGER NOT NULL,
              is_prerelease   INTEGER NOT NULL,     -- derived at ingest for "latest" computation (D6)
              PRIMARY KEY (repo, package_id, version),
              FOREIGN KEY (repo, package_id) REFERENCES catalog_packages(repo, id)
                          ON DELETE CASCADE
            );

            CREATE TABLE catalog_architectures (
              repo        TEXT NOT NULL,
              package_id  TEXT NOT NULL,
              version     TEXT NOT NULL,
              arch        TEXT NOT NULL,            -- x64 | arm64 | x86
              PRIMARY KEY (repo, package_id, version, arch),
              FOREIGN KEY (repo, package_id, version) REFERENCES catalog_versions
                          ON DELETE CASCADE
            );

            -- catalog: dependencies VERBATIM from the index (D6); qualification
            -- (A9) is resolved at plan time, never rewritten at ingest
            CREATE TABLE catalog_dependencies (
              repo        TEXT NOT NULL,
              package_id  TEXT NOT NULL,
              version     TEXT NOT NULL,
              dep_id      TEXT NOT NULL,            -- as written; may be repo-qualified
              dep_range   TEXT NOT NULL,            -- NuGet interval notation, verbatim (D3)
              position    INTEGER NOT NULL,         -- preserves manifest order
              PRIMARY KEY (repo, package_id, version, position),
              FOREIGN KEY (repo, package_id, version) REFERENCES catalog_versions
                          ON DELETE CASCADE
            );
            CREATE INDEX idx_dep_reverse ON catalog_dependencies(dep_id);

            -- installed state (A9/A10)
            CREATE TABLE installed_packages (
              repo        TEXT NOT NULL,
              package_id  TEXT NOT NULL,
              version     TEXT NOT NULL,
              arch        TEXT NOT NULL,            -- re-selection records the change, A10
              install_type TEXT NOT NULL CHECK (install_type IN ('portable')),
              explicit    INTEGER NOT NULL,         -- 1 requested, 0 pulled as dep (A10)
              installed_at TEXT NOT NULL,
              updated_at  TEXT NOT NULL,
              PRIMARY KEY (repo, package_id)        -- one active version per identity
            );
            CREATE INDEX idx_installed_explicit ON installed_packages(explicit);

            CREATE TABLE dependency_edges (         -- every edge, A10
              dependent_repo TEXT NOT NULL,
              dependent_id   TEXT NOT NULL,
              dep_repo       TEXT NOT NULL,
              dep_id         TEXT NOT NULL,
              PRIMARY KEY (dependent_repo, dependent_id, dep_repo, dep_id),
              FOREIGN KEY (dependent_repo, dependent_id)
                REFERENCES installed_packages(repo, package_id) ON DELETE CASCADE
            );
            CREATE INDEX idx_edges_by_dep ON dependency_edges(dep_repo, dep_id);

            CREATE TABLE pins (                     -- cleanup never removes pinned (A10)
              repo        TEXT NOT NULL,
              package_id  TEXT NOT NULL,
              pinned_at   TEXT NOT NULL,
              PRIMARY KEY (repo, package_id),
              FOREIGN KEY (repo, package_id) REFERENCES installed_packages
                          ON DELETE CASCADE
            );

            -- shim ownership lives in the database, never the hash (D4)
            CREATE TABLE shims (
              name        TEXT PRIMARY KEY,         -- bin\<name>.exe
              owner_repo  TEXT NOT NULL,
              owner_pkg   TEXT NOT NULL,
              owner_version TEXT NOT NULL,
              target_path TEXT NOT NULL,            -- absolute; containment per D4
              subsystem   TEXT NOT NULL CHECK (subsystem IN ('console','gui')),
              template_sha256 TEXT NOT NULL,        -- doctor compares (D4)
              created_at  TEXT NOT NULL,
              updated_at  TEXT NOT NULL,
              FOREIGN KEY (owner_repo, owner_pkg) REFERENCES installed_packages
                          ON DELETE CASCADE
            );
            CREATE INDEX idx_shims_by_owner ON shims(owner_repo, owner_pkg);

            -- journal (A11): operations + ordered steps, intent before / outcome after
            CREATE TABLE operations (
              op_id       TEXT PRIMARY KEY,         -- = staging\<operation-id> (A6)
              type        TEXT NOT NULL CHECK (type IN
                          ('install','upgrade','uninstall','bundle','selfinstall',
                           'selfupdate','selfuninstall','repoadd','reporemove')),
              state       TEXT NOT NULL CHECK (state IN
                          ('Planned','Downloaded','Verified','Extracted','Validated',
                           'Placed','ShimsActivated','StateCommitted','Completed',
                           'Compensating','RolledBack','RecoveryFailed')),
              command     TEXT NOT NULL,
              error       TEXT,                     -- exact inconsistency report (A11)
              started_at  TEXT NOT NULL,
              updated_at  TEXT NOT NULL
            );
            CREATE INDEX idx_ops_open ON operations(state)
              WHERE state NOT IN ('Completed','RolledBack','RecoveryFailed');

            CREATE TABLE operation_steps (
              op_id       TEXT NOT NULL REFERENCES operations ON DELETE CASCADE,
              seq         INTEGER NOT NULL,
              name        TEXT NOT NULL,            -- checkpoint name (PLAN §6)
              intent_json TEXT NOT NULL,            -- written BEFORE the action
              outcome_json TEXT,                    -- written AFTER; NULL = not reached
              state       TEXT NOT NULL,
              created_at  TEXT NOT NULL,
              updated_at  TEXT NOT NULL,
              PRIMARY KEY (op_id, seq)
            );

            -- every durable file side-effect, including .old.<guid> renames (D4);
            -- cleanup deletes ONLY rows here, never pattern matches
            CREATE TABLE journal_files (
              op_id       TEXT NOT NULL REFERENCES operations ON DELETE CASCADE,
              seq         INTEGER NOT NULL,
              path        TEXT NOT NULL,            -- absolute, same volume (A6)
              kind        TEXT NOT NULL CHECK (kind IN
                          ('old_shim','old_dir','staging','sidecar')),
              created_at  TEXT NOT NULL,
              deleted_at  TEXT,                     -- NULL until cleanup succeeds
              PRIMARY KEY (op_id, seq, path)
            );
            CREATE INDEX idx_files_pending ON journal_files(path)
              WHERE deleted_at IS NULL;
            """),
    ];
}
