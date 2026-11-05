using WinBrew.Core;
using WinBrew.Infrastructure;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>A6 layout creation and the one-volume refusal.</summary>
public sealed class StorageLayoutTests : IDisposable
{
    private readonly string _parent;

    public StorageLayoutTests()
    {
        _parent = Path.Combine(Path.GetTempPath(), "wbrew-layout-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_parent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Ensure_CreatesEveryDirectory_AndReportsOk()
    {
        var root = Path.Combine(_parent, "wbroot");
        // StoragePaths resolves WINBREW_ROOT — set it for this test.
        Environment.SetEnvironmentVariable(StoragePaths.RootEnvironmentVariable, root);
        try
        {
            var layout = new StoragePaths(new FakeEnvironmentProbe { SameVolumeAnswer = true });
            Assert.Equal(root, layout.Root);

            var status = await layout.EnsureAsync(CancellationToken.None);
            Assert.True(status.RootExists);
            Assert.True(status.AllDirectoriesPresent);
            Assert.True(status.Writable);

            foreach (var dir in new[] { "app", "packages", "bin", "cache", "staging", "logs", "state" })
            {
                Assert.True(Directory.Exists(layout.Directories[dir]), $"{dir} missing");
            }

            Assert.True(Directory.Exists(Path.Combine(root, "cache", "artifacts")));
            Assert.True(Directory.Exists(Path.Combine(root, "state", "backups")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StoragePaths.RootEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task VolumeConflict_RefusedWithConflicOrRefused() // A6: atomic renames require one volume
    {
        var root = Path.Combine(_parent, "wbroot2");
        Environment.SetEnvironmentVariable(StoragePaths.RootEnvironmentVariable, root);
        try
        {
            // The layout must EXIST first — the volume check inspects real
            // directories (missing ones are skipped), so create it with a
            // friendly probe, then re-check with a hostile one.
            var initial = new StoragePaths(new FakeEnvironmentProbe { SameVolumeAnswer = true });
            await initial.EnsureAsync(CancellationToken.None);

            var layout = new StoragePaths(new FakeEnvironmentProbe { SameVolumeAnswer = false });

            var ex = await Assert.ThrowsAsync<WinBrewException>(() => layout.EnsureAsync(CancellationToken.None));
            Assert.Equal(WinBrewErrorCode.ConflictOrRefused, ex.Code);
            Assert.Contains("same volume", ex.Message);
            Assert.Contains("staging", ex.Message); // the offending directory is named
        }
        finally
        {
            Environment.SetEnvironmentVariable(StoragePaths.RootEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task MissingRoot_Inspect_DoesNotCreate()
    {
        var root = Path.Combine(_parent, "wbroot3");
        Environment.SetEnvironmentVariable(StoragePaths.RootEnvironmentVariable, root);
        try
        {
            var layout = new StoragePaths(new FakeEnvironmentProbe());
            var status = await layout.InspectAsync(CancellationToken.None);
            Assert.False(status.RootExists);
            Assert.False(Directory.Exists(root)); // doctor never mutates (read-only)
        }
        finally
        {
            Environment.SetEnvironmentVariable(StoragePaths.RootEnvironmentVariable, null);
        }
    }
}
