using Microsoft.Data.Sqlite;
using SQLitePCL;
using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Infrastructure.Sqlite;

/// <summary>
/// SQLite state database (A6): WAL mode, foreign keys ON, forward-only
/// user_version migrations with a backup copy in state\backups\ before each
/// migration. Never downgrades; a newer schema than the binary supports is a
/// clear error (exit 1).
/// </summary>
public sealed class SqliteStateDatabase : IStateDatabase
{
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SqliteStateDatabase(string databasePath, string backupDirectory)
    {
        _databasePath = databasePath;
        _backupDirectory = backupDirectory;
    }

    public int MaxSupportedSchemaVersion => Migrations.LatestVersion;

    public async Task<int> GetCurrentSchemaVersionAsync(CancellationToken ct)
    {
        if (!File.Exists(_databasePath))
        {
            return -1;
        }

        await using var connection = await OpenAsync(readOnly: true, ct).ConfigureAwait(false);
        return GetVersion(connection);
    }

    public async Task ApplyMigrationsAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        Directory.CreateDirectory(_backupDirectory);

        await using var connection = await OpenAsync(readOnly: false, ct).ConfigureAwait(false);
        var current = GetVersion(connection);

        if (current > MaxSupportedSchemaVersion)
        {
            throw new WinBrewException(
                WinBrewErrorCode.GeneralFailure,
                $"The state database schema is v{current}, but this WinBrew build supports at most " +
                $"v{MaxSupportedSchemaVersion}. Schema downgrades are never performed (A6); " +
                "install a newer wbrew build.",
                new Dictionary<string, object?>
                {
                    ["databaseSchemaVersion"] = current,
                    ["supportedSchemaVersion"] = MaxSupportedSchemaVersion,
                });
        }

        foreach (var migration in Migrations.All.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await BackupAsync(connection, migration.Version, ct).ConfigureAwait(false);

            await using var transaction = connection.BeginTransaction();
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.Ddl;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var setVersion = connection.CreateCommand();
            setVersion.Transaction = transaction;
            setVersion.CommandText = $"PRAGMA user_version = {migration.Version};";
            await setVersion.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            transaction.Commit();
        }
    }

    /// <summary>
    /// The FTS5 probe (Phase A): CREATE VIRTUAL TABLE … USING fts5 succeeds
    /// when the bundled SQLite build includes FTS5. Runs against the SAME
    /// bundle that ships with wbrew (PROBES.md §4 warns not to assume this
    /// from any other build).
    /// </summary>
    public async Task<bool> SupportsFts5Async(CancellationToken ct)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE temp.fts5probe USING fts5(content);";
        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // ---- internals -------------------------------------------------------

    private async Task<SqliteConnection> OpenAsync(bool readOnly, CancellationToken ct)
    {
        // A4: initialize the bundled SQLitePCLRaw provider before first use.
        Batteries_V2.Init();

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = readOnly && File.Exists(_databasePath) ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        };

        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var pragmas = connection.CreateCommand();
        // WAL and foreign keys are per-connection settings (A6).
        pragmas.CommandText = readOnly && File.Exists(_databasePath)
            ? "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;"
            : "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        await pragmas.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return connection;
    }

    private static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task BackupAsync(SqliteConnection connection, int targetVersion, CancellationToken ct)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(_backupDirectory, $"winbrew-pre-v{targetVersion}-{stamp}.db");

        await using var backupConnection = new SqliteConnection($"Data Source={backupPath}");
        await backupConnection.OpenAsync(ct).ConfigureAwait(false);
        connection.BackupDatabase(backupConnection); // SQLite backup API (A6)
    }
}
