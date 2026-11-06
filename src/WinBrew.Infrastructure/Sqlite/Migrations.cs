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
    public const int LatestVersion = 3;

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
        new(2, "trust state, self-update trust, FTS5 search index", """
            -- D6: the accepted root (exact envelope bytes) and expiries per
            -- repository; role high-water marks stay in role_versions (v1)
            CREATE TABLE repository_trust (
              repo        TEXT PRIMARY KEY REFERENCES repositories(name) ON DELETE CASCADE,
              root_envelope BLOB NOT NULL,
              root_fingerprints TEXT NOT NULL,      -- sorted root-role key IDs, comma-separated
              timestamp_expires TEXT NOT NULL,      -- ISO-8601 UTC
              index_expires TEXT NOT NULL,
              verified_at TEXT NOT NULL             -- last full verification (offline age, A15)
            );

            -- A14: the self-update feed uses the same root chain and marks
            CREATE TABLE self_update_trust (
              scope       TEXT PRIMARY KEY CHECK (scope = 'self'),
              root_envelope BLOB NOT NULL,
              root_version INTEGER NOT NULL,
              timestamp_version INTEGER NOT NULL,
              index_version INTEGER NOT NULL,
              verified_at TEXT NOT NULL
            );

            -- D6 search (ADR-0005): FTS5 trigram index over the SAME search_text
            -- (lowercased with ToLowerInvariant at ingest). case_sensitive 1:
            -- case folding is never delegated to SQLite.
            CREATE VIRTUAL TABLE catalog_search USING fts5(
              search_text, repo UNINDEXED, id UNINDEXED,
              tokenize = 'trigram case_sensitive 1'
            );
            INSERT INTO catalog_search(search_text, repo, id)
              SELECT search_text, repo, id FROM catalog_packages;
            """),
        new(3, "installer-managed packages (v1.1)", """
            -- v1.1 (A12): MSI/EXE packages. Kept apart from installed_packages
            -- (portable: shims, dependency edges, retention) — installer packages
            -- have none of those. Per-machine installs are recorded here too and
            -- reconciled through the ProductCode (MsiQueryProductState).
            CREATE TABLE installer_packages (
              repo        TEXT NOT NULL,
              package_id  TEXT NOT NULL,
              version     TEXT NOT NULL,
              arch        TEXT NOT NULL,
              install_type TEXT NOT NULL CHECK (install_type IN ('msi','exe')),
              scope       TEXT NOT NULL CHECK (scope IN ('user','machine')),
              product_code TEXT,                    -- MSI ProductCode (braced GUID)
              upgrade_code TEXT,
              state       TEXT NOT NULL CHECK (state IN ('Installed','RebootRequired','Unknown')),
              log_path    TEXT,
              installer_json TEXT NOT NULL,         -- the declaration at install time (uninstall input)
              installed_at TEXT NOT NULL,
              updated_at  TEXT NOT NULL,
              PRIMARY KEY (repo, package_id)
            );
            """),
    ];
}
