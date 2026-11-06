using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using WinBrew.Application.Installers;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Packages;
using WinBrew.Application.Ports;
using WinBrew.Application.SelfManagement;
using WinBrew.Cli.Output;

namespace WinBrew.Cli;

/// <summary>
/// Package commands: install, upgrade, uninstall, list, info, recover, self install. Handlers parse, call ONE use
/// case and render (A5); consent follows A8 (--yes approves the plan only;
/// --modify-path is its own consent; non-interactive + missing consent = 10).
/// </summary>
internal static class PackageCommands
{
    public static void Build(RootCommand root, Command self, Lazy<IServiceProvider> services)
    {
        root.Subcommands.Add(BuildInstall(services));
        root.Subcommands.Add(BuildUpgrade(services));
        MaintenanceCommands.Build(root, services);
        root.Subcommands.Add(BuildUninstall(services));
        root.Subcommands.Add(BuildList(services));
        root.Subcommands.Add(BuildInfo(services));
        root.Subcommands.Add(BuildRecover(services));
        self.Subcommands.Add(BuildSelfInstall(services));
    }

    // ---- install / upgrade ------------------------------------------------------------------

    private static Command BuildInstall(Lazy<IServiceProvider> services)
    {
        var package = new Argument<string>("package") { Description = "<package>[@<version>] — '<id>' or '<repo>/<id>' (A9)." };
        var version = new Option<string?>("--version") { Description = "Exact version to install (not together with @<version>, A8)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the plan from the existing catalog; change nothing (A8)." };
        var allowInstaller = AllowInstallerOption();
        var scope = ScopeOption();

        var command = new Command("install", "Install a package and its dependencies (D2: resolved before any change); MSI/EXE need --allow-installer.");
        command.Arguments.Add(package);
        command.Options.Add(version);
        command.Options.Add(dryRun);
        command.Options.Add(allowInstaller);
        command.Options.Add(scope);

        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("install", json, async token =>
            {
                var sp = services.Value;
                var nonInteractive = await IsNonInteractiveAsync(sp, parseResult, token);
                var request = new InstallRequest(parseResult.GetRequiredValue(package), parseResult.GetValue(version),
                    parseResult.GetValue(dryRun), parseResult.GetValue(GlobalOptions.Offline),
                    new InstallerOptions(parseResult.GetValue(allowInstaller), parseResult.GetValue(scope) ?? "user"));
                var result = await sp.GetRequiredService<InstallUseCase>().ExecuteAsync(request, Approver(sp, parseResult, nonInteractive, json), token);
                RenderPlanResult(result, json);
                return new CommandOutput(result, result.Warnings);
            }, ct);
        });

        return command;
    }

    private static Command BuildUpgrade(Lazy<IServiceProvider> services)
    {
        var package = new Argument<string?>("package") { Description = "Package to upgrade (default: every unpinned installed package).", Arity = ArgumentArity.ZeroOrOne };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the plan from the existing catalog; change nothing (A8)." };
        var command = new Command("upgrade", "Upgrade installed packages to the highest satisfying versions (D2 rule 3).");
        command.Arguments.Add(package);
        command.Options.Add(dryRun);

        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("upgrade", json, async token =>
            {
                var sp = services.Value;
                var nonInteractive = await IsNonInteractiveAsync(sp, parseResult, token);
                var result = await sp.GetRequiredService<UpgradeUseCase>().ExecuteAsync(
                    new UpgradeRequest(parseResult.GetValue(package), parseResult.GetValue(dryRun), parseResult.GetValue(GlobalOptions.Offline)),
                    Approver(sp, parseResult, nonInteractive, json), token);
                RenderPlanResult(result, json);
                return new CommandOutput(result, result.Warnings);
            }, ct);
        });

        return command;
    }

    internal static PlanApproval Approver(IServiceProvider sp, ParseResult parseResult, bool nonInteractive, bool json) =>
        async (plan, t) =>
        {
            if (!json)
            {
                RenderPlan(plan);
            }

            return await ApproveAsync(sp, parseResult, nonInteractive, t);
        };

    internal static void RenderPlanResult(PlanResult result, bool json)
    {
        if (json)
        {
            return;
        }

        RenderRecovered(result.Recovered);
        if (result.DryRun)
        {
            RenderPlan(result.Plan);
            Console.Out.WriteLine("dry run: nothing was changed.");
        }
        else if (!result.Changed)
        {
            Console.Out.WriteLine("Nothing to do: " + string.Join(", ", result.Plan.Items.Select(i => $"{i.Package} {i.Version}")) + " already installed.");
        }
        else
        {
            var changed = result.Plan.Items.Where(i => i.Changes).Select(i => $"{i.Package} {i.Version} ({i.Arch})");
            Console.Out.WriteLine("Done: " + string.Join(", ", changed) +
                (result.Shims.Count > 0 ? $"; commands: {string.Join(", ", result.Shims)}" : string.Empty) + ".");
        }
    }

    /// <summary>The full plan (A8): every package of the resolved graph, dependencies first.</summary>
    internal static void RenderPlan(OperationPlan plan)
    {
        Console.Out.WriteLine($"Plan ({plan.Command}):");
        foreach (var item in plan.Items)
        {
            var line = item.Action switch
            {
                "upgrade" => $"  upgrade  {item.Package} {item.PreviousVersion} → {item.Version}" +
                             (item.ArchitectureTransition is { } t ? $" [{t}]" : $" ({item.Arch})"),
                "install" => $"  install  {item.Package} {item.Version} ({item.Arch})",
                _ => $"  keep     {item.Package} {item.Version} ({item.Arch})",
            };
            if (!item.Explicit)
            {
                line += " [dependency]";
            }

            if (item.ArchitectureFallback && item.Changes)
            {
                line += " [architecture fallback, A10]";
            }

            Console.Out.WriteLine(line);
        }
    }

    // ---- uninstall -----------------------------------------------------------------------

    private static Command BuildUninstall(Lazy<IServiceProvider> services)
    {
        var package = new Argument<string>("package") { Description = "'<id>' or '<repo>/<id>'." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print what would be removed; change nothing." };
        var allowInstaller = AllowInstallerOption();
        var command = new Command("uninstall", "Uninstall a package (refused while another package depends on it, D8); MSI/EXE need --allow-installer.");
        command.Arguments.Add(package);
        command.Options.Add(dryRun);
        command.Options.Add(allowInstaller);

        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("uninstall", json, async token =>
            {
                var sp = services.Value;
                var nonInteractive = await IsNonInteractiveAsync(sp, parseResult, token);
                var result = await sp.GetRequiredService<UninstallUseCase>().ExecuteAsync(
                    new UninstallRequest(parseResult.GetRequiredValue(package), parseResult.GetValue(dryRun),
                        new InstallerOptions(parseResult.GetValue(allowInstaller), "user")),
                    async (plan, t) =>
                    {
                        if (!json)
                        {
                            RenderUninstallPlan(plan);
                        }

                        return await ApproveAsync(sp, parseResult, nonInteractive, t);
                    }, token);

                if (!json)
                {
                    RenderRecovered(result.Recovered);
                    if (result.DryRun)
                    {
                        RenderUninstallPlan(result.Plan);
                        Console.Out.WriteLine("dry run: nothing was changed.");
                    }
                    else
                    {
                        Console.Out.WriteLine($"Uninstalled {result.Plan.Repo}/{result.Plan.Id} {result.Plan.Version}.");
                    }
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });

        return command;
    }

    private static void RenderUninstallPlan(UninstallPlan plan)
    {
        Console.Out.WriteLine($"Plan: uninstall {plan.Repo}/{plan.Id} {plan.Version} ({plan.Arch})" +
            (plan.Shims.Count > 0 ? $"; removes commands: {string.Join(", ", plan.Shims)}" : string.Empty));
    }

    // ---- list / info (read-only: no lock, D5) -------------------------------------------------

    private static Command BuildList(Lazy<IServiceProvider> services)
    {
        var command = new Command("list", "List installed packages.");
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("list", json, async token =>
            {
                var result = await services.Value.GetRequiredService<ListUseCase>().ExecuteAsync(token);
                if (!json)
                {
                    if (result.Packages.Count == 0 && result.Installers.Count == 0)
                    {
                        Console.Out.WriteLine("No packages installed.");
                    }

                    foreach (var p in result.Packages)
                    {
                        Console.Out.WriteLine($"{p.Package} {p.Version} ({p.Arch})" + (p.Explicit ? string.Empty : " [dependency]"));
                    }

                    foreach (var p in result.Installers)
                    {
                        Console.Out.WriteLine($"{p.Package} {p.Version} ({p.Arch}) [{p.Type}, {p.Scope}, {p.State}]" + (p.Drift is null ? "" : " DRIFT"));
                    }
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });
        return command;
    }

    private static Command BuildInfo(Lazy<IServiceProvider> services)
    {
        var package = new Argument<string>("package") { Description = "'<id>' or '<repo>/<id>'." };
        var command = new Command("info", "Show package details from the catalog and installed state.");
        command.Arguments.Add(package);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("info", json, async token =>
            {
                var info = await services.Value.GetRequiredService<InfoUseCase>()
                    .ExecuteAsync(parseResult.GetRequiredValue(package), parseResult.GetValue(GlobalOptions.Offline), token);
                if (!json)
                {
                    Console.Out.WriteLine($"{info.Package}: {info.DisplayName}");
                    Console.Out.WriteLine($"  {info.Description}");
                    Console.Out.WriteLine($"  latest for this host: {info.Latest ?? "(none)"}");
                    Console.Out.WriteLine($"  versions: {string.Join(", ", info.Versions.Select(v => v.Version + " [" + string.Join("/", v.Architectures) + "]"))}");
                    Console.Out.WriteLine(info.Installed is { } i ? $"  installed: {i.Version} ({i.Arch})" : "  installed: no");
                }

                return new CommandOutput(info, info.Warnings);
            }, ct);
        });
        return command;
    }

    // ---- recover -------------------------------------------------------------------------------

    private static Command BuildRecover(Lazy<IServiceProvider> services)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "List what recovery would do; change nothing." };
        var command = new Command("recover", "Recover interrupted operations from the journal (A11).");
        command.Options.Add(dryRun);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("recover", json, async token =>
            {
                var result = await services.Value.GetRequiredService<RecoverUseCase>().ExecuteAsync(parseResult.GetValue(dryRun), token);
                if (!json)
                {
                    if (result.Operations.Count == 0)
                    {
                        Console.Out.WriteLine("Nothing to recover.");
                    }

                    foreach (var op in result.Operations)
                    {
                        Console.Out.WriteLine($"{op.Type} {op.OpId}: {op.FromState} → {op.ToState} ({op.Action})");
                    }

                    if (result.PendingFileCleanups > 0)
                    {
                        Console.Out.WriteLine($"{result.PendingFileCleanups} journaled file(s) are still locked; they are retried later.");
                    }
                }

                return result;
            }, ct);
        });
        return command;
    }

    internal static void RenderRecovered(IReadOnlyList<RecoveredOperation> recovered)
    {
        foreach (var op in recovered)
        {
            Console.Error.WriteLine($"recovered interrupted {op.Type} {op.OpId}: {op.FromState} → {op.ToState}");
        }
    }

    // ---- self install ----------------------------------------------------------------------

    private static Command BuildSelfInstall(Lazy<IServiceProvider> services)
    {
        var modifyPath = new Option<bool>("--modify-path") { Description = "Consent to adding WinBrew's bin\\ to the user PATH (never implied by --yes, A8)." };
        var command = new Command("install", "Install WinBrew from the extracted release zip into %LOCALAPPDATA%\\WinBrew (A14).");
        command.Options.Add(modifyPath);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("self install", json, async token =>
            {
                var sp = services.Value;
                var nonInteractive = await IsNonInteractiveAsync(sp, parseResult, token);
                var prompt = sp.GetRequiredService<IConsentPrompt>();
                var result = await sp.GetRequiredService<SelfInstallUseCase>().ExecuteAsync(
                    new SelfInstallRequest(parseResult.GetValue(modifyPath)),
                    async (bin, t) => nonInteractive ? null : await prompt.ConfirmAsync($"Add {bin} to your user PATH?", false, t),
                    token);

                if (!json)
                {
                    Console.Out.WriteLine($"WinBrew {result.Version} installed in {result.AppDirectory}; shim: {result.SelfShim}" +
                        (result.PathChanged ? " (user PATH updated)." : "."));
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });
        return command;
    }

    private static Option<bool> AllowInstallerOption() => new("--allow-installer")
    {
        Description = "Consent to running an MSI/EXE installer (executable code chosen by the repository). Never implied by --yes (A8).",
    };

    private static Option<string?> ScopeOption()
    {
        var option = new Option<string?>("--scope")
        {
            Description = "user (default) or machine: per-machine installs run through the elevated helper (UAC prompt, A12).",
        };
        option.AcceptOnlyFromAmong("user", "machine");
        return option;
    }

    // ---- consent (A8) --------------------------------------------------------------------------

    internal static async Task<bool> IsNonInteractiveAsync(IServiceProvider sp, ParseResult parseResult, CancellationToken ct)
    {
        string? configInteractive = null;
        var doc = await sp.GetRequiredService<IConfigStore>().LoadAsync(ct);
        if (doc.Values.TryGetValue("interactive", out var value) && value is string s)
        {
            configInteractive = s;
        }

        return new InteractivityOptions(
            parseResult.GetValue(GlobalOptions.NonInteractive),
            configInteractive,
            Environment.GetEnvironmentVariable("WINBREW_NONINTERACTIVE"),
            StdinIsTerminal: !Console.IsInputRedirected).IsNonInteractive;
    }

    /// <summary>--yes approves the displayed plan; otherwise prompt, or exit 10 when non-interactive (A8).</summary>
    internal static Task<bool> ApproveAsync(IServiceProvider sp, ParseResult parseResult, bool nonInteractive, CancellationToken ct)
    {
        if (parseResult.GetValue(GlobalOptions.Yes))
        {
            return Task.FromResult(true);
        }

        return sp.GetRequiredService<IConsentPrompt>().ConfirmAsync("Proceed with this plan?", nonInteractive, ct);
    }
}
