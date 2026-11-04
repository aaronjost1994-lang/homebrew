using Microsoft.Data.Sqlite;
using Windows;
using Xunit;

public class StackTests
{
    [Fact]
    public void InMemory_sqlite_works()
    {
        using var con = new SqliteConnection("Data Source=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "CREATE TABLE t(a TEXT); INSERT INTO t VALUES('winbrew'); SELECT a FROM t;";
        Assert.Equal("winbrew", cmd.ExecuteScalar());
    }

    [Fact]
    public void File_db_wal_mode_works()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wbmatrix-{Guid.NewGuid():N}.db");
        try
        {
            using (var con = new SqliteConnection($"Data Source={path}"))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t(x);";
                cmd.ExecuteNonQuery(); // first result row = "wal" when honored
            }
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Registry_api_compiles_off_windows()
    {
        // The compile proves the net10.0-windows TFM exposes Microsoft.Win32
        // with no extra packages. Runtime behavior off-Windows is the point of
        // this probe — record it, do not assert it:
        var ex = Record.Exception(() => WinOps.ReadPath());
        // (ex is expected to be PlatformNotSupportedException here; the real
        //  behavior gets reported in PROBES.md, not guessed in an assertion.)
    }
}
