using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WinBrew.Application.Configuration;
using WinBrew.Application.Doctor;
using WinBrew.Application.Ports;
using WinBrew.Infrastructure;
using WinBrew.Infrastructure.Config;
using WinBrew.Infrastructure.FaultInjection;
using WinBrew.Infrastructure.Locking;
using WinBrew.Infrastructure.Sqlite;
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

        // Use cases (one per command handler, A5).
        builder.Services.AddSingleton<GetConfigValueUseCase>();
        builder.Services.AddSingleton<SetConfigValueUseCase>();
        builder.Services.AddSingleton<ListConfigUseCase>();
        builder.Services.AddSingleton<DoctorUseCase>(sp => new DoctorUseCase(
            sp.GetRequiredService<IVersionInfo>(),
            sp.GetRequiredService<IStorageLayout>(),
            sp.GetRequiredService<IConfigStore>(),
            sp.GetRequiredService<IStateDatabase>(),
            sp.GetRequiredService<IMutationLock>(),
            sp.GetRequiredService<IEnvironmentProbe>(),
            testHooks));

        return builder.Build().Services;
    }
}
