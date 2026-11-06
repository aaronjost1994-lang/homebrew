using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WinBrew.Application.Catalog;
using WinBrew.Application.Configuration;
using WinBrew.Application.Doctor;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Packages;
using WinBrew.Application.Ports;
using WinBrew.Application.SelfManagement;
using WinBrew.Infrastructure;
using WinBrew.Infrastructure.Archive;
using WinBrew.Infrastructure.Config;
using WinBrew.Infrastructure.FaultInjection;
using WinBrew.Infrastructure.Files;
using WinBrew.Infrastructure.Http;
using WinBrew.Infrastructure.Locking;
using WinBrew.Infrastructure.Sqlite;
using WinBrew.Infrastructure.Trust;
using WinBrew.Windows;

namespace WinBrew.Cli;

/// <summary>
/// Composition root (A5): the ONLY place that references Infrastructure and
/// Windows for registration. Handlers get services through the lazy factory
/// — the DI container is never built during parsing and never for
/// fast-path commands (A4).
/// </summary>
internal static class ServiceConfigurator
{
    /// <summary>True only in test-hooks builds (-p:WinBrewTestHooks=true, A11/A13).</summary>
    internal static bool TestHooksBuild
    {
        get
        {
#if WINBREW_TEST_HOOKS
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// HKCU\Environment, or — in TEST-HOOKS builds with WINBREW_TEST_MODE=1
    /// only — the disposable key named by WINBREW_TEST_PATH_KEY (A16: PATH
    /// tests run against a test hive, never the real user PATH). Compiled out
    /// of release builds like every other test-only behavior (A11).
    /// </summary>
    private static string UserPathKey(WinBrew.Application.Lifecycle.RuntimeFlags flags)
    {
#if WINBREW_TEST_HOOKS
        var testKey = Environment.GetEnvironmentVariable("WINBREW_TEST_PATH_KEY");
        if (flags.TestHooksBuild && flags.TestModeEnvironment && !string.IsNullOrWhiteSpace(testKey))
        {
            return testKey;
        }
#else
        _ = flags;
#endif
        return RegistryUserPathEditor.EnvironmentKey;
    }

    public static IServiceProvider BuildProvider()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "wbrew",
        });

        var testHooks = TestHooksBuild;

        // Windows probes (composition root choice; the alternative Linux
        // behaviors are exercised through fakes in tests).
        builder.Services.AddSingleton<IEnvironmentProbe, WindowsEnvironmentProbe>();

        builder.Services.AddSingleton<StoragePaths>();
        builder.Services.AddSingleton<IStorageLayout>(sp => sp.GetRequiredService<StoragePaths>());

        builder.Services.AddSingleton<IConfigStore>(sp => new JsonConfigStore(
            sp.GetRequiredService<StoragePaths>().ConfigFilePath,
            testHooks));

        builder.Services.AddSingleton<IStateDatabase>(sp =>
        {
            var paths = sp.GetRequiredService<StoragePaths>();
            return new SqliteStateDatabase(paths.DatabasePath, paths.Directories["backups"]);
        });

        builder.Services.AddSingleton<IMutationLock>(sp => new ProcessMutationLock(
            sp.GetRequiredService<StoragePaths>().LockFilePath));

        builder.Services.AddSingleton<IFaultInjector>(_ => FaultInjectorFactory.Create(testHooks));

        builder.Services.AddSingleton<IConsentPrompt, ConsoleConsentPrompt>();
        builder.Services.AddSingleton<IVersionInfo, CliVersionInfo>();

        // ---- Phase B: portable lifecycle ports ----------------------------------------
        var flags = new RuntimeFlags(testHooks, Environment.GetEnvironmentVariable("WINBREW_TEST_MODE") == "1");
        builder.Services.AddSingleton(flags);
        builder.Services.AddSingleton(TimeProvider.System);

        // A13/A4: HttpClient via IHttpClientFactory; redirects are followed
        // manually, no cookies, no automatic decompression (sizes are checked
        // on the bytes received), and the factory's logging handlers are
        // removed so no URL (with its query string) is ever logged unredacted.
        builder.Services.AddHttpClient(SecureHttpFetcher.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(30),
            })
            .RemoveAllLoggers();

        builder.Services.AddSingleton<ICredentialProvider, CredentialManagerProvider>();
        builder.Services.AddSingleton<IFileLockInspector, RestartManagerInspector>();
        builder.Services.AddSingleton<IHttpFetcher, SecureHttpFetcher>();
        builder.Services.AddSingleton<IPackageFileSystem, PackageFileSystem>();
        builder.Services.AddSingleton<IArchiveExtractor, SafeZipExtractor>();
        builder.Services.AddSingleton<IArtifactCache>(sp => new ArtifactCache(
            sp.GetRequiredService<StoragePaths>().Directories["cache.artifacts"],
            sp.GetRequiredService<IPackageFileSystem>()));
        builder.Services.AddSingleton(sp => new SqliteStateStore(
            sp.GetRequiredService<StoragePaths>().DatabasePath, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ICatalogStore>(sp => sp.GetRequiredService<SqliteStateStore>());
        builder.Services.AddSingleton<IInstalledStore>(sp => sp.GetRequiredService<SqliteStateStore>());
        builder.Services.AddSingleton<IOperationJournal>(sp => sp.GetRequiredService<SqliteStateStore>());
        builder.Services.AddSingleton<IShimStore>(sp => new ShimStore(
            sp.GetRequiredService<StoragePaths>().Directories["bin"], sp.GetRequiredService<IPackageFileSystem>()));
        builder.Services.AddSingleton<IUserPathEditor>(_ => new RegistryUserPathEditor(UserPathKey(flags)));
        builder.Services.AddSingleton<ISelfLocation, CliSelfLocation>();
        builder.Services.AddSingleton(_ => MetadataVerifierFactory.Create(flags.TestHooksBuild, flags.TestModeEnvironment));

        builder.Services.AddSingleton(sp => new LifecycleServices(
            sp.GetRequiredService<IStorageLayout>(),
            sp.GetRequiredService<IOperationJournal>(),
            sp.GetRequiredService<IFaultInjector>(),
            sp.GetRequiredService<IPackageFileSystem>(),
            sp.GetRequiredService<IShimStore>(),
            sp.GetRequiredService<IInstalledStore>(),
            sp.GetRequiredService<ICatalogStore>(),
            sp.GetRequiredService<IArtifactCache>(),
            sp.GetRequiredService<IArchiveExtractor>(),
            sp.GetRequiredService<IHttpFetcher>(),
            sp.GetRequiredService<IUserPathEditor>(),
            sp.GetRequiredService<IConfigStore>(),
            sp.GetRequiredService<ISelfLocation>(),
            sp.GetRequiredService<IEnvironmentProbe>(),
            flags,
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<RecoveryService>();
        builder.Services.AddSingleton<MutationSession>();

        // Use cases (one per command handler, A5).
        builder.Services.AddSingleton<GetConfigValueUseCase>();
        builder.Services.AddSingleton<SetConfigValueUseCase>();
        builder.Services.AddSingleton<ListConfigUseCase>();
        builder.Services.AddSingleton<ResolutionPlanner>();
        builder.Services.AddSingleton<PlanExecutor>();
        builder.Services.AddSingleton<InstallUseCase>();
        builder.Services.AddSingleton<UpgradeUseCase>();
        builder.Services.AddSingleton<OutdatedUseCase>();
        builder.Services.AddSingleton<PinUseCase>();
        builder.Services.AddSingleton<AutoremoveUseCase>();
        builder.Services.AddSingleton<CleanupUseCase>();
        builder.Services.AddSingleton<UninstallUseCase>();
        builder.Services.AddSingleton<ListUseCase>();
        builder.Services.AddSingleton<InfoUseCase>();
        builder.Services.AddSingleton<RecoverUseCase>();
        builder.Services.AddSingleton<SelfInstallUseCase>();
        builder.Services.AddSingleton<IngestIndexUseCase>();
        builder.Services.AddSingleton<LifecycleDoctorChecks>();
        builder.Services.AddSingleton<DoctorUseCase>(sp => new DoctorUseCase(
            sp.GetRequiredService<IVersionInfo>(),
            sp.GetRequiredService<IStorageLayout>(),
            sp.GetRequiredService<IConfigStore>(),
            sp.GetRequiredService<IStateDatabase>(),
            sp.GetRequiredService<IMutationLock>(),
            sp.GetRequiredService<IEnvironmentProbe>(),
            testHooks,
            sp.GetRequiredService<LifecycleDoctorChecks>()));

        return builder.Build().Services;
    }
}
