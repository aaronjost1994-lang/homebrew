using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using WinBrew.Application.Configuration;
using WinBrew.Application.Doctor;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Config;
using WinBrew.Cli.Output;

namespace WinBrew.Cli;

/// <summary>
/// The System.CommandLine 2.0 stable command tree for every A8 command.
/// Commands outside Phases A–B return a documented NotAvailableInThisBuild
/// error through the normal error path (exit 1) — phase-a.md.
/// Handlers only parse input, call one use case and render (A5).
/// </summary>
internal static class CommandTree
{
    internal static RootCommand Build(Lazy<IServiceProvider> services)
    {
        // RootCommand's name comes from the entry assembly, which is 'wbrew' (A4).
        var root = new RootCommand("WinBrew package manager");

        GlobalOptions.AddToRoot(root);

        BuildConfig(root, services);
        BuildDoctor(root, services);
        var self = BuildNotAvailableCommands(root);
        PackageCommands.Build(root, self, services);

        return root;
    }

    // ---- config (Phase A) ------------------------------------------------

    private static void BuildConfig(RootCommand root, Lazy<IServiceProvider> services)
    {
        var configCommand = new Command("config", "View and set configuration keys (D10).");

        var getKeyArg = new Argument<string>("key") { Description = "Configuration key (D10)." };
        var configGet = new Command("get", "Get the effective value of a configuration key.");
        configGet.Arguments.Add(getKeyArg);
        configGet.SetAction((parseResult, ct) =>
        {
            var key = parseResult.GetRequiredValue(getKeyArg);
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("config get", json, async token =>
            {
                var (k, value, isDefault) = await services.Value
                    .GetRequiredService<GetConfigValueUseCase>()
                    .ExecuteAsync(key, token)
                    .ConfigureAwait(false);

                if (!json)
                {
                    Console.Out.WriteLine(isDefault
                        ? $"{k} = {value} (default)"
                        : $"{k} = {value}");
                }

                return new ConfigGetData(k, value, isDefault);
            }, ct);
        });

        var setKeyArg = new Argument<string>("key") { Description = "Configuration key (D10)." };
        var setValueArg = new Argument<string>("value") { Description = "Value to set." };
        var configSet = new Command("set", "Validate and set a configuration value.");
        configSet.Arguments.Add(setKeyArg);
        configSet.Arguments.Add(setValueArg);
        configSet.SetAction((parseResult, ct) =>
        {
            var key = parseResult.GetRequiredValue(setKeyArg);
            var value = parseResult.GetRequiredValue(setValueArg);
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("config set", json, async token =>
            {
                // Mutating command (D5): takes the mutation lock.
                var sp = services.Value;
                var timeout = await GetLockTimeoutAsync(sp, token).ConfigureAwait(false);
                using var lockHandle = await sp.GetRequiredService<IMutationLock>()
                    .AcquireAsync("config set", timeout, token).ConfigureAwait(false);

                // Mutating commands ensure the layout first (A6).
                await sp.GetRequiredService<IStorageLayout>().EnsureAsync(token).ConfigureAwait(false);

                var (k, effective) = await sp.GetRequiredService<SetConfigValueUseCase>()
                    .ExecuteAsync(key, value, ServiceConfigurator.TestHooksBuild, token)
                    .ConfigureAwait(false);

                if (!json)
                {
                    Console.Out.WriteLine($"{k} = {effective}");
                }

                return new ConfigSetData(k, effective);
            }, ct);
        });

        var configList = new Command("list", "List every configuration key with its effective value.");
        configList.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("config list", json, async token =>
            {
                var rows = await services.Value.GetRequiredService<ListConfigUseCase>()
                    .ExecuteAsync(token).ConfigureAwait(false);

                if (!json)
                {
                    foreach (var (key, value, isDefault) in rows)
                    {
                        Console.Out.WriteLine(isDefault
                            ? $"{key} = {value} (default)"
                            : $"{key} = {value}");
                    }
                }

                return new ConfigListData(rows
                    .Select(r => new ConfigValueRow(r.Key, r.Value, r.IsDefault))
                    .ToList());
            }, ct);
        });

        configCommand.Subcommands.Add(configGet);
        configCommand.Subcommands.Add(configSet);
        configCommand.Subcommands.Add(configList);
        root.Subcommands.Add(configCommand);
    }

    private static async Task<TimeSpan> GetLockTimeoutAsync(IServiceProvider sp, CancellationToken ct)
    {
        var config = await sp.GetRequiredService<IConfigStore>().LoadAsync(ct).ConfigureAwait(false);
        int seconds = (int)ConfigDefinitions.Keys["lock.timeoutSeconds"].Default;
        if (config.Values.TryGetValue("lock.timeoutSeconds", out var value) && value is int i)
        {
            seconds = i;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    // ---- doctor (Phase A) --------------------------------------------------

    private static void BuildDoctor(RootCommand root, Lazy<IServiceProvider> services)
    {
        var doctorCommand = new Command("doctor", "Check the WinBrew environment and report problems (A8).");
        doctorCommand.SetAction(async (parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            DoctorReport report = null!;
            var code = await CommandRunner.RunAsync("doctor", json, async token =>
            {
                report = await services.Value.GetRequiredService<DoctorUseCase>()
                    .ExecuteAsync(token).ConfigureAwait(false);

                if (!json)
                {
                    RenderDoctorText(report);
                }

                return report;
            }, ct).ConfigureAwait(false);

            // Doctor "succeeds" when its checks ran; a failing check still
            // means exit 1 (GeneralFailure) so scripts can react.
            if (code == 0 && report is { Healthy: false })
            {
                return 1;
            }

            return code;
        });
        root.Subcommands.Add(doctorCommand);
    }

    private static void RenderDoctorText(DoctorReport report)
    {
        Console.Out.WriteLine($"wbrew {report.WinBrewVersion} doctor");
        Console.Out.WriteLine($"root: {report.Root}");
        foreach (var check in report.Checks)
        {
            var marker = check.Status switch
            {
                DoctorStatus.Ok => "[ ok ]",
                DoctorStatus.Warn => "[warn]",
                _ => "[FAIL]",
            };
            Console.Out.WriteLine($"{marker} {check.Name}: {check.Detail}");
        }

        Console.Out.WriteLine(report.Healthy
            ? "doctor: no failures."
            : "doctor: FAILURES present — see above (exit 1).");
    }

    // ---- not-available commands (outside Phases A–B) ------------------------

    private static Command BuildNotAvailableCommands(RootCommand root)
    {
        // Each maps to the phase that delivers it, per the phase map (README/PLAN §7).
        root.Subcommands.Add(NotAvailable("search", "Search the catalog", "Phase D (catalog)"));
        root.Subcommands.Add(NotAvailable("autoremove", "Remove dependency-only packages", "Phase C (orphans, D8)"));
        root.Subcommands.Add(NotAvailable("outdated", "List packages with newer versions", "Phase C (installed state display)"));
        root.Subcommands.Add(NotAvailable("update", "Refresh repository metadata", "Phase D (trust)"));
        root.Subcommands.Add(NotAvailable("upgrade", "Upgrade installed packages", "Phase C (upgrade compensation)"));
        root.Subcommands.Add(NotAvailable("pin", "Pin a package", "Phase C (pins)"));
        root.Subcommands.Add(NotAvailable("unpin", "Unpin a package", "Phase C (pins)"));
        root.Subcommands.Add(NotAvailable("cleanup", "Reclaim cache space", "Phase C (cache budget)"));

        var repo = new Command("repo", "Manage configured repositories (D6).");
        repo.Subcommands.Add(NotAvailable("add", "Add a repository", "Phase D (trust bootstrap)"));
        repo.Subcommands.Add(NotAvailable("list", "List configured repositories", "Phase D"));
        repo.Subcommands.Add(NotAvailable("remove", "Remove a repository", "Phase D"));
        root.Subcommands.Add(repo);

        var bundle = new Command("bundle", "Export and install package bundles (A15).");
        bundle.Subcommands.Add(NotAvailable("export", "Export a bundle", "Phase D"));
        bundle.Subcommands.Add(NotAvailable("install", "Install a bundle", "Phase D"));
        root.Subcommands.Add(bundle);

        var self = new Command("self", "Manage the WinBrew installation itself (A14).");
        self.Subcommands.Add(NotAvailable("update", "Self-update from the signed release feed", "Phase D (A14)"));
        self.Subcommands.Add(NotAvailable("uninstall", "Remove WinBrew", "Phase D (A14)"));
        root.Subcommands.Add(self);
        return self;
    }

    /// <summary>
    /// A command outside this phase's scope: documents itself, then fails
    /// through the NORMAL error path with exit 1 (phase-a.md). It never
    /// silently succeeds and never pretends to run.
    /// </summary>
    private static Command NotAvailable(string name, string description, string phase)
    {
        var command = new Command(name, $"{description} [not available in this build — planned for {phase}]");
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync(name, json, _ => throw new WinBrewException(
                WinBrewErrorCode.GeneralFailure,
                $"'wbrew {name}' is not available in this build (NotAvailableInThisBuild; planned for {phase}).",
                new Dictionary<string, object?>
                {
                    ["reason"] = "NotAvailableInThisBuild",
                    ["command"] = name,
                    ["phase"] = phase,
                }), ct);
        });
        return command;
    }
}
