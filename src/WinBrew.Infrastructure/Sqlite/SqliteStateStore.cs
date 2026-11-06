using System.Globalization;
using Microsoft.Data.Sqlite;
using NuGet.Versioning;
using SQLitePCL;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Catalog;
using WinBrew.Core.Operations;

namespace WinBrew.Infrastructure.Sqlite;

/// <summary>
/// SQLite implementation of the catalog (D6), installed state (A9/A10, D4)
/// and the operation journal (A11) over schema v1. Read paths never create
/// the database: with no state\winbrew.db they return empty results (A6:
/// read-only commands never migrate). Write paths expect the mutating
/// session to have applied migrations already.
/// </summary>
public sealed class SqliteStateStore(string databasePath, TimeProvider time) : ICatalogStore, IInstalledStore, IOperationJournal
{
    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private bool Exists => File.Exists(databasePath);

    private async Task<SqliteConnection> OpenAsync(bool write, CancellationToken ct)
    {
        Batteries_V2.Init();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            DefaultTimeout = 30,
        };

        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct);
        await using var pragmas = connection.CreateCommand();
        pragmas.CommandText = write
            ? "PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;"
            : "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
        await pragmas.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? tx, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task ExecAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] p)
    {
        await using var command = Command(c, sql, tx, p);
        await command.ExecuteNonQueryAsync(ct);
    }

    // ==== catalog (D6) =================================================================

    public async Task IngestAsync(RepositoryRecord repository, RepositoryIndex index, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        var now = Now();

        await ExecAsync(c, tx, """
            INSERT INTO repositories(name, url, created_at) VALUES ($n, $u, $t)
            ON CONFLICT(name) DO UPDATE SET url = excluded.url
            """, ct, ("$n", repository.Name), ("$u", repository.Url), ("$t", now));

        // Replay/rollback protection (D6): never accept a lower index version.
        await using (var read = Command(c, "SELECT accepted_version FROM role_versions WHERE repo=$r AND role='index'", tx, ("$r", repository.Name)))
        {
            var current = await read.ExecuteScalarAsync(ct);
            if (current is long accepted && index.Version < accepted)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"index.json version {index.Version} for '{repository.Name}' is lower than the accepted version {accepted} " +
                    "(rollback/replay rejected, D6).",
                    new Dictionary<string, object?> { ["accepted"] = accepted, ["offered"] = index.Version });
            }
        }

        // Replace this repository's catalog (cascades remove versions/archs/deps).
        await ExecAsync(c, tx, "DELETE FROM catalog_packages WHERE repo=$r", ct, ("$r", repository.Name));

        foreach (var (id, package) in index.Packages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var searchText = $"{id} {package.DisplayName} {package.Description}".ToLowerInvariant();
            await ExecAsync(c, tx, """
                INSERT INTO catalog_packages(repo, id, display_name, description, search_text)
                VALUES ($r, $i, $d, $desc, $s)
                """, ct, ("$r", repository.Name), ("$i", id), ("$d", package.DisplayName), ("$desc", package.Description), ("$s", searchText));

            foreach (var (version, entry) in package.Versions)
            {
                var prerelease = NuGetVersion.Parse(version).IsPrerelease;
                await ExecAsync(c, tx, """
                    INSERT INTO catalog_versions(repo, package_id, version, manifest_sha256, manifest_size, is_prerelease)
                    VALUES ($r, $i, $v, $sha, $size, $pre)
                    """, ct, ("$r", repository.Name), ("$i", id), ("$v", version), ("$sha", entry.Sha256),
                    ("$size", entry.Size), ("$pre", prerelease ? 1 : 0));

                foreach (var arch in entry.Architectures)
                {
                    await ExecAsync(c, tx, """
                        INSERT INTO catalog_architectures(repo, package_id, version, arch) VALUES ($r, $i, $v, $a)
                        """, ct, ("$r", repository.Name), ("$i", id), ("$v", version), ("$a", arch));
                }

                for (var position = 0; position < entry.Dependencies.Count; position++)
                {
                    var dependency = entry.Dependencies[position];
                    await ExecAsync(c, tx, """
                        INSERT INTO catalog_dependencies(repo, package_id, version, dep_id, dep_range, position)
                        VALUES ($r, $i, $v, $d, $range, $p)
                        """, ct, ("$r", repository.Name), ("$i", id), ("$v", version), ("$d", dependency.Id),
                        ("$range", dependency.Range), ("$p", position));
                }
            }
        }

        await ExecAsync(c, tx, """
            INSERT INTO role_versions(repo, role, accepted_version, accepted_at) VALUES ($r, 'index', $v, $t)
            ON CONFLICT(repo, role) DO UPDATE SET accepted_version = excluded.accepted_version, accepted_at = excluded.accepted_at
            """, ct, ("$r", repository.Name), ("$v", index.Version), ("$t", now));

        await tx.CommitAsync(ct);
    }

    public async Task<RepositoryRecord?> GetRepositoryAsync(string name, CancellationToken ct)
    {
        if (!Exists)
        {
            return null;
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, "SELECT name, url FROM repositories WHERE name=$n", null, ("$n", name));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new RepositoryRecord(r.GetString(0), r.GetString(1)) : null;
    }

    public async Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesAsync(CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, "SELECT name, url FROM repositories ORDER BY name", null);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RepositoryRecord>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new RepositoryRecord(r.GetString(0), r.GetString(1)));
        }

        return list;
    }

    public async Task<IReadOnlyList<string>> FindRepositoriesForAsync(string id, CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, "SELECT repo FROM catalog_packages WHERE id=$i ORDER BY repo", null, ("$i", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct))
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    public async Task<CatalogPackage?> GetPackageAsync(string repo, string id, CancellationToken ct)
    {
        if (!Exists)
        {
            return null;
        }

        await using var c = await OpenAsync(write: false, ct);
        string displayName, description;
        await using (var cmd = Command(c, "SELECT display_name, description FROM catalog_packages WHERE repo=$r AND id=$i", null, ("$r", repo), ("$i", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct))
            {
                return null;
            }

            displayName = r.GetString(0);
            description = r.GetString(1);
        }

        var archs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var cmd = Command(c, "SELECT version, arch FROM catalog_architectures WHERE repo=$r AND package_id=$i ORDER BY arch", null, ("$r", repo), ("$i", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var version = r.GetString(0);
                if (!archs.TryGetValue(version, out var list))
                {
                    archs[version] = list = [];
                }

                list.Add(r.GetString(1));
            }
        }

        var deps = new Dictionary<string, List<IndexDependency>>(StringComparer.Ordinal);
        await using (var cmd = Command(c, "SELECT version, dep_id, dep_range FROM catalog_dependencies WHERE repo=$r AND package_id=$i ORDER BY version, position", null, ("$r", repo), ("$i", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var version = r.GetString(0);
                if (!deps.TryGetValue(version, out var list))
                {
                    deps[version] = list = [];
                }

                list.Add(new IndexDependency(r.GetString(1), r.GetString(2)));
            }
        }

        var versions = new List<CatalogVersion>();
        await using (var cmd = Command(c, "SELECT version, manifest_sha256, manifest_size, is_prerelease FROM catalog_versions WHERE repo=$r AND package_id=$i", null, ("$r", repo), ("$i", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var version = r.GetString(0);
                versions.Add(new CatalogVersion(version, r.GetString(1), r.GetInt64(2), r.GetInt64(3) != 0,
                    archs.TryGetValue(version, out var a) ? a : [],
                    deps.TryGetValue(version, out var d) ? d : []));
            }
        }

        return new CatalogPackage(repo, id, displayName, description, versions);
    }

    public async Task<int?> GetAcceptedVersionAsync(string repo, string role, CancellationToken ct)
    {
        if (!Exists)
        {
            return null;
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, "SELECT accepted_version FROM role_versions WHERE repo=$r AND role=$role", null, ("$r", repo), ("$role", role));
        return await cmd.ExecuteScalarAsync(ct) is long v ? (int)v : null;
    }

    // ==== installed state ================================================================

    private static InstalledPackage ReadInstalled(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetString(5), r.GetString(6));

    private const string InstalledColumns = "repo, package_id, version, arch, explicit, installed_at, updated_at";

    public async Task<InstalledPackage?> GetInstalledAsync(string repo, string id, CancellationToken ct)
    {
        if (!Exists)
        {
            return null;
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, $"SELECT {InstalledColumns} FROM installed_packages WHERE repo=$r AND package_id=$i", null, ("$r", repo), ("$i", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadInstalled(r) : null;
    }

    public async Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, $"SELECT {InstalledColumns} FROM installed_packages ORDER BY repo, package_id", null);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<InstalledPackage>();
        while (await r.ReadAsync(ct))
        {
            list.Add(ReadInstalled(r));
        }

        return list;
    }

    private const string ShimColumns = "name, owner_repo, owner_pkg, owner_version, target_path, subsystem, template_sha256";

    private static ShimRecord ReadShim(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6));

    public async Task<ShimRecord?> GetShimAsync(string name, CancellationToken ct)
    {
        if (!Exists)
        {
            return null;
        }

        await using var c = await OpenAsync(write: false, ct);
        // Windows file names are case-insensitive: so is shim ownership.
        await using var cmd = Command(c, $"SELECT {ShimColumns} FROM shims WHERE name=$n COLLATE NOCASE", null, ("$n", name));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadShim(r) : null;
    }

    public async Task<IReadOnlyList<ShimRecord>> GetShimsForAsync(string repo, string id, CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, $"SELECT {ShimColumns} FROM shims WHERE owner_repo=$r AND owner_pkg=$i ORDER BY name", null, ("$r", repo), ("$i", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ShimRecord>();
        while (await r.ReadAsync(ct))
        {
            list.Add(ReadShim(r));
        }

        return list;
    }

    public async Task<IReadOnlyList<string>> GetDependentsAsync(string repo, string id, CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, """
            SELECT dependent_repo || '/' || dependent_id FROM dependency_edges
            WHERE dep_repo=$r AND dep_id=$i ORDER BY 1
            """, null, ("$r", repo), ("$i", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct))
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    public async Task CommitPlanAsync(string opId, IReadOnlyList<InstallCommit> commits, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        var now = Now();

        foreach (var commit in commits)
        {
            var p = commit.Package;
            await ExecAsync(c, tx, """
                INSERT INTO installed_packages(repo, package_id, version, arch, install_type, explicit, installed_at, updated_at)
                VALUES ($r, $i, $v, $a, 'portable', $e, $ia, $ua)
                ON CONFLICT(repo, package_id) DO UPDATE SET
                  version = excluded.version, arch = excluded.arch, explicit = MAX(installed_packages.explicit, excluded.explicit),
                  updated_at = excluded.updated_at
                """, ct, ("$r", p.Repo), ("$i", p.Id), ("$v", p.Version), ("$a", p.Arch), ("$e", p.Explicit ? 1 : 0),
                ("$ia", p.InstalledAt), ("$ua", p.UpdatedAt));

            foreach (var name in commit.RemovedShimNames)
            {
                await ExecAsync(c, tx, "DELETE FROM shims WHERE name=$n COLLATE NOCASE AND owner_repo=$r AND owner_pkg=$i", ct,
                    ("$n", name), ("$r", p.Repo), ("$i", p.Id));
            }

            foreach (var shim in commit.Shims)
            {
                await ExecAsync(c, tx, """
                    INSERT INTO shims(name, owner_repo, owner_pkg, owner_version, target_path, subsystem, template_sha256, created_at, updated_at)
                    VALUES ($n, $r, $i, $v, $t, $s, $h, $now, $now)
                    ON CONFLICT(name) DO UPDATE SET
                      owner_version = excluded.owner_version, target_path = excluded.target_path,
                      subsystem = excluded.subsystem, template_sha256 = excluded.template_sha256, updated_at = excluded.updated_at
                    """, ct, ("$n", shim.Name), ("$r", shim.OwnerRepo), ("$i", shim.OwnerPackage), ("$v", shim.OwnerVersion),
                    ("$t", shim.TargetPath), ("$s", shim.Subsystem), ("$h", shim.TemplateSha256), ("$now", now));
            }

            // Every edge is recorded (A10); a package's edges are replaced with its current dependencies.
            await ExecAsync(c, tx, "DELETE FROM dependency_edges WHERE dependent_repo=$r AND dependent_id=$i", ct, ("$r", p.Repo), ("$i", p.Id));
            foreach (var dep in commit.Dependencies)
            {
                var slash = dep.IndexOf('/', StringComparison.Ordinal);
                await ExecAsync(c, tx, """
                    INSERT OR IGNORE INTO dependency_edges(dependent_repo, dependent_id, dep_repo, dep_id) VALUES ($r, $i, $dr, $di)
                    """, ct, ("$r", p.Repo), ("$i", p.Id), ("$dr", dep[..slash]), ("$di", dep[(slash + 1)..]));
            }
        }

        await SetStateInTransactionAsync(c, tx, opId, OperationState.StateCommitted, null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<DependencyEdge>> ListDependencyEdgesAsync(CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, """
            SELECT dependent_repo || '/' || dependent_id, dep_repo || '/' || dep_id FROM dependency_edges ORDER BY 1, 2
            """, null);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DependencyEdge>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new DependencyEdge(r.GetString(0), r.GetString(1)));
        }

        return list;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPinsAsync(CancellationToken ct)
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Exists)
        {
            return pins;
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, """
            SELECT p.repo || '/' || p.package_id, i.version FROM pins p
            JOIN installed_packages i ON i.repo = p.repo AND i.package_id = p.package_id ORDER BY 1
            """, null);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            pins[r.GetString(0)] = r.GetString(1);
        }

        return pins;
    }

    public async Task<bool> SetPinnedAsync(string repo, string id, bool pinned, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await using var cmd = Command(c, pinned
            ? "INSERT OR IGNORE INTO pins(repo, package_id, pinned_at) VALUES ($r, $i, $t)"
            : "DELETE FROM pins WHERE repo=$r AND package_id=$i", null, ("$r", repo), ("$i", id), ("$t", Now()));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task CommitUninstallAsync(string opId, string repo, string id, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        // Cascades remove shims, dependency edges and pins (schema v1).
        await ExecAsync(c, tx, "DELETE FROM installed_packages WHERE repo=$r AND package_id=$i", ct, ("$r", repo), ("$i", id));
        await SetStateInTransactionAsync(c, tx, opId, OperationState.StateCommitted, null, ct);
        await tx.CommitAsync(ct);
    }

    // ==== journal (A11) ====================================================================

    public async Task CreateAsync(string opId, OperationType type, string command, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        var now = Now();
        await ExecAsync(c, null, """
            INSERT INTO operations(op_id, type, state, command, error, started_at, updated_at)
            VALUES ($o, $type, 'Planned', $cmd, NULL, $now, $now)
            """, ct, ("$o", opId), ("$type", OperationTypeWire.ToWire(type)), ("$cmd", command), ("$now", now));
    }

    public async Task<int> BeginStepAsync(string opId, string name, string intentJson, OperationState state, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var next = Command(c, "SELECT COALESCE(MAX(seq), 0) + 1 FROM operation_steps WHERE op_id=$o", tx, ("$o", opId));
        var seq = Convert.ToInt32(await next.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        var now = Now();
        await ExecAsync(c, tx, """
            INSERT INTO operation_steps(op_id, seq, name, intent_json, outcome_json, state, created_at, updated_at)
            VALUES ($o, $seq, $n, $intent, NULL, $state, $now, $now)
            """, ct, ("$o", opId), ("$seq", seq), ("$n", name), ("$intent", intentJson),
            ("$state", OperationStateWire.ToWire(state)), ("$now", now));
        await tx.CommitAsync(ct);
        return seq;
    }

    public async Task CompleteStepAsync(string opId, int seq, string outcomeJson, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await ExecAsync(c, null, "UPDATE operation_steps SET outcome_json=$out, updated_at=$now WHERE op_id=$o AND seq=$seq", ct,
            ("$out", outcomeJson), ("$now", Now()), ("$o", opId), ("$seq", seq));
    }

    public async Task SetStateAsync(string opId, OperationState state, string? failureReport, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await SetStateInTransactionAsync(c, null, opId, state, failureReport, ct);
    }

    private async Task SetStateInTransactionAsync(SqliteConnection c, SqliteTransaction? tx, string opId, OperationState state, string? error, CancellationToken ct)
    {
        await ExecAsync(c, tx, "UPDATE operations SET state=$s, error=COALESCE($e, error), updated_at=$now WHERE op_id=$o", ct,
            ("$s", OperationStateWire.ToWire(state)), ("$e", error), ("$now", Now()), ("$o", opId));
    }

    public async Task RecordFileAsync(string opId, int seq, string path, string kind, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await ExecAsync(c, null, """
            INSERT OR IGNORE INTO journal_files(op_id, seq, path, kind, created_at, deleted_at)
            VALUES ($o, $seq, $p, $k, $now, NULL)
            """, ct, ("$o", opId), ("$seq", seq), ("$p", path), ("$k", kind), ("$now", Now()));
    }

    public async Task MarkFileDeletedAsync(JournalFile file, CancellationToken ct)
    {
        await using var c = await OpenAsync(write: true, ct);
        await ExecAsync(c, null, "UPDATE journal_files SET deleted_at=$now WHERE op_id=$o AND seq=$seq AND path=$p", ct,
            ("$now", Now()), ("$o", file.OpId), ("$seq", file.Seq), ("$p", file.Path));
    }

    public async Task<IReadOnlyList<JournalFile>> GetPendingCleanupAsync(CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, """
            SELECT f.op_id, f.seq, f.path, f.kind FROM journal_files f
            JOIN operations o ON o.op_id = f.op_id
            WHERE f.deleted_at IS NULL AND o.state IN ('Completed','RolledBack','RecoveryFailed')
            ORDER BY f.created_at
            """, null);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<JournalFile>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new JournalFile(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3)));
        }

        return list;
    }

    private const string OperationColumns = "op_id, type, state, command, error, started_at, updated_at";

    private static OperationRecord ReadOperation(SqliteDataReader r)
    {
        OperationTypeWire.TryFromWire(r.GetString(1), out var type);
        OperationStateWire.TryFromWire(r.GetString(2), out var state);
        return new OperationRecord(r.GetString(0), type, state, r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6));
    }

    private async Task<IReadOnlyList<OperationRecord>> QueryOperationsAsync(string where, CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, $"SELECT {OperationColumns} FROM operations WHERE {where} ORDER BY started_at, op_id", null);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<OperationRecord>();
        while (await r.ReadAsync(ct))
        {
            list.Add(ReadOperation(r));
        }

        return list;
    }

    public Task<IReadOnlyList<OperationRecord>> GetIncompleteAsync(CancellationToken ct) =>
        QueryOperationsAsync("state NOT IN ('Completed','RolledBack','RecoveryFailed')", ct);

    public Task<IReadOnlyList<OperationRecord>> GetRecoveryFailedAsync(CancellationToken ct) =>
        QueryOperationsAsync("state = 'RecoveryFailed'", ct);

    public async Task<OperationRecord?> GetAsync(string opId, CancellationToken ct)
    {
        if (!Exists)
        {
            return null;
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, $"SELECT {OperationColumns} FROM operations WHERE op_id=$o", null, ("$o", opId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadOperation(r) : null;
    }

    public async Task<IReadOnlyList<OperationStep>> GetStepsAsync(string opId, CancellationToken ct)
    {
        if (!Exists)
        {
            return [];
        }

        await using var c = await OpenAsync(write: false, ct);
        await using var cmd = Command(c, "SELECT seq, name, intent_json, outcome_json, state FROM operation_steps WHERE op_id=$o ORDER BY seq", null, ("$o", opId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<OperationStep>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new OperationStep(r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        }

        return list;
    }
}
