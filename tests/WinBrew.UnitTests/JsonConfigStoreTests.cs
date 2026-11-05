using WinBrew.Core;
using WinBrew.Infrastructure.Config;

namespace WinBrew.UnitTests;

/// <summary>config.json load/validate/save behavior (D10) over real temp files.</summary>
public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _root;

    public JsonConfigStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wbrew-config-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    private string ConfigPath => Path.Combine(_root, "config.json");

    private JsonConfigStore CreateStore(bool testHooks = false) => new(ConfigPath, testHooks);

    private async Task WriteConfigAsync(string json) =>
        await File.WriteAllTextAsync(ConfigPath, json);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task MissingFile_LoadsEmptyFileValues_StrictAndInspectionClean()
    {
        var doc = await CreateStore().LoadAsync(CancellationToken.None);
        Assert.Empty(doc.Values); // defaults come from ConfigDefinitions, not the file

        var inspection = await CreateStore().InspectAsync(CancellationToken.None);
        Assert.Null(inspection.Document);
        Assert.Empty(inspection.Problems);
    }

    [Fact]
    public async Task ValidFile_LoadsTypedValues()
    {
        await WriteConfigAsync(
            """{"schemaVersion":"1","values":{"color":"never","lock.timeoutSeconds":5,"path.managed":true,"cache.maxBytes":100}}""");

        var doc = await CreateStore().LoadAsync(CancellationToken.None);
        Assert.Equal("never", doc.Values["color"]);
        Assert.Equal(5, doc.Values["lock.timeoutSeconds"]);
        Assert.Equal(true, doc.Values["path.managed"]);
        Assert.Equal(100L, doc.Values["cache.maxBytes"]); // wide ints load as long
    }

    [Fact]
    public async Task UnknownKey_StrictLoadThrows_DoesNotTouchDefaults() // D10 exit 2
    {
        await WriteConfigAsync("""{"schemaVersion":"1","values":{"mystery.key":1}}""");

        var ex = await Assert.ThrowsAsync<WinBrewException>(() => CreateStore().LoadAsync(CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.UsageError, ex.Code);
        Assert.Contains("mystery.key", ex.Message);
    }

    [Fact]
    public async Task OutOfRangeValue_StrictLoadThrows()
    {
        await WriteConfigAsync("""{"schemaVersion":"1","values":{"download.retries":9}}""");
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => CreateStore().LoadAsync(CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.UsageError, ex.Code);
    }

    [Fact]
    public async Task InvalidJson_StrictLoadThrows_InspectionReports()
    {
        await WriteConfigAsync("{ not json ");

        await Assert.ThrowsAsync<WinBrewException>(() => CreateStore().LoadAsync(CancellationToken.None));

        var inspection = await CreateStore().InspectAsync(CancellationToken.None);
        Assert.Null(inspection.Document);
        Assert.Single(inspection.Problems);
        Assert.Equal("(file)", inspection.Problems[0].Key);
    }

    [Fact]
    public async Task TestingKey_InFile_InspectionReportsForDoctor_StrictLoadRejectsInRelease() // D10
    {
        await WriteConfigAsync("""{"schemaVersion":"1","values":{"testing.allowInsecureLocalhost":true}}""");

        // Release build: strict load throws (honored nowhere).
        await Assert.ThrowsAsync<WinBrewException>(() => CreateStore(testHooks: false).LoadAsync(CancellationToken.None));

        // Doctor sees it as a reportable problem (warn material, not a crash).
        var inspection = await CreateStore(testHooks: false).InspectAsync(CancellationToken.None);
        Assert.Contains(inspection.Problems, p => p.Key == "testing.allowInsecureLocalhost");
    }

    [Fact]
    public async Task TestingKey_InTestHooksBuild_LoadsCleanly()
    {
        await WriteConfigAsync("""{"schemaVersion":"1","values":{"testing.allowInsecureLocalhost":true}}""");
        var doc = await CreateStore(testHooks: true).LoadAsync(CancellationToken.None);
        Assert.Equal(true, doc.Values["testing.allowInsecureLocalhost"]);
    }

    [Fact]
    public async Task Save_WritesSchemaVersionAndTypedValues_AtomicTempCleanup()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["lock.timeoutSeconds"] = 12,
            ["color"] = "always",
        };

        await CreateStore().SaveAsync(values, CancellationToken.None);

        var json = await File.ReadAllTextAsync(ConfigPath);
        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("lock.timeoutSeconds", json);
        Assert.Contains("12", json); // typed value persisted
        Assert.False(File.Exists(ConfigPath + ".tmp")); // atomic write left no temp behind

        // Round trip.
        var doc = await CreateStore().LoadAsync(CancellationToken.None);
        Assert.Equal(12, doc.Values["lock.timeoutSeconds"]);
        Assert.Equal("always", doc.Values["color"]);
    }

    [Fact]
    public async Task Save_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_root, "nested", "config.json");
        var store = new JsonConfigStore(nested, testHooksBuild: false);
        await store.SaveAsync(new Dictionary<string, object?> { ["retainVersions"] = 3 }, CancellationToken.None);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task WrongSchemaVersion_Reported()
    {
        await WriteConfigAsync("""{"schemaVersion":"9","values":{}}""");
        var inspection = await CreateStore().InspectAsync(CancellationToken.None);
        Assert.Contains(inspection.Problems, p => p.Key == "schemaVersion");
    }
}
