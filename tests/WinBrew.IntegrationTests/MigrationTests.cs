using Microsoft.Data.Sqlite;
using WinBrew.Core;
using WinBrew.Infrastructure.Sqlite;

namespace WinBrew.IntegrationTests;

/// <summary>
/// Real-SQLite migration tests (A16): empty → v1, backup before migrate,
/// idempotency, and the never-downgrade refusal.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _dir;

    public MigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wbrew-migration-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    private string DbPath => Path.Combine(_dir, "winbrew.db");
    private string BackupDir => Path.Combine(_dir, "backups");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task EmptyToV1_Migrates_AndCreatesBackup() // phase-a acceptance
    {
        var db = new SqliteStateDatabase(DbPath, BackupDir);

        Assert.Equal(-1, await db.GetCurrentSchemaVersionAsync(CancellationToken.None)); // no file yet
        await db.ApplyMigrationsAsync(CancellationToken.None);

        Assert.Equal(1, await db.GetCurrentSchemaVersionAsync(CancellationToken.None));

        var backups = Directory.GetFiles(BackupDir);
        Assert.Single(backups);
        Assert.Contains("pre-v1", Path.GetFileName(backups[0]));

        // The backup is a real SQLite copy of the PRE-migration state
        // (A6: backup before every migration — for empty→v1 that state is
        // an empty database at user_version 0).
        await using var backupConnection = new SqliteConnection($"Data Source={backups[0]}");
        await backupConnection.OpenAsync();
        using var version = backupConnection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(0, Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SchemaV1_CreatesEveryPlannedTable() // PLAN §5
    {
        var db = new SqliteStateDatabase(DbPath, BackupDir);
        await db.ApplyMigrationsAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        var expected = new[]
        {
            "repositories", "role_versions", "catalog_packages", "catalog_versions",
            "catalog_architectures", "catalog_dependencies", "installed_packages",
            "dependency_edges", "pins", "shims", "operations", "operation_steps", "journal_files",
        };

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        var actual = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetString(0));
        }

        foreach (var table in expected)
        {
            Assert.Contains(table, actual);
        }
    }

    [Fact]
    public async Task Migrations_AreIdempotent_SecondRunNoNewBackup()
    {
        var db = new SqliteStateDatabase(DbPath, BackupDir);
        await db.ApplyMigrationsAsync(CancellationToken.None);
        var backupsAfterFirst = Directory.GetFiles(BackupDir).Length;

        await db.ApplyMigrationsAsync(CancellationToken.None); // nothing to do at v1

        Assert.Equal(backupsAfterFirst, Directory.GetFiles(BackupDir).Length);
        Assert.Equal(1, await db.GetCurrentSchemaVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NewerSchemaThanBinary_RefusedWithClearError_Exit1() // A6: never downgrade
    {
        // Simulate a database written by a FUTURE WinBrew.
        Directory.CreateDirectory(BackupDir);
        await using (var future = new SqliteConnection($"Data Source={DbPath}"))
        {
            await future.OpenAsync();
            using var command = future.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var db = new SqliteStateDatabase(DbPath, BackupDir);
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => db.ApplyMigrationsAsync(CancellationToken.None));

        Assert.Equal(WinBrewErrorCode.GeneralFailure, ex.Code); // exit 1 per A6
        Assert.Contains("never", ex.Message);
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatabaseIs_WalMode_AfterOpen()
    {
        var db = new SqliteStateDatabase(DbPath, BackupDir);
        await db.ApplyMigrationsAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!.ToLowerInvariant());
    }

    [Fact]
    public async Task SupportsFts5_TrueOnPinnedBundle() // the Phase A probe as a permanent test (ADR-0005)
    {
        var db = new SqliteStateDatabase(DbPath, BackupDir);
        Assert.True(await db.SupportsFts5Async(CancellationToken.None));
    }
}
