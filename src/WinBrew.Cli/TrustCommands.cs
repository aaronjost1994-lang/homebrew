using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using WinBrew.Application.Bundles;
using WinBrew.Application.Catalog;
using WinBrew.Application.Ports;
using WinBrew.Application.SelfManagement;
using WinBrew.Cli.Output;

namespace WinBrew.Cli;

/// <summary>
/// Phase D commands: repo add/list/remove, update, search (D6), bundle
/// export/install (A15), self update / self uninstall (A14). Handlers parse,
/// call ONE use case and render (A5). Consent follows A8: --yes approves the
/// displayed plan only; adding a trust root needs --trust-root or an
/// interactive approval; non-interactive + missing consent exits 10.
/// </summary>
internal static class TrustCommands
{
    public static void Build(RootCommand root, Command self, Lazy<IServiceProvider> services)
    {
        root.Subcommands.Add(BuildSearch(services));
        root.Subcommands.Add(BuildUpdate(services));
        root.Subcommands.Add(BuildRepo(services));
        root.Subcommands.Add(BuildBundle(services));
        self.Subcommands.Add(BuildSelfUpdate(services));
        self.Subcommands.Add(BuildSelfUninstall(services));
    }

    // ---- search / update ------------------------------------------------------------------

    private static Command BuildSearch(Lazy<IServiceProvider> services)
    {
        var query = new Argument<string>("query") { Description = "Case-insensitive prefix or substring of id, name or description (D6)." };
        var command = new Command("search", "Search the verified catalog (read-only; never refreshes metadata).");
        command.Arguments.Add(query);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("search", json, async token =>
            {
                var result = await services.Value.GetRequiredService<SearchUseCase>().ExecuteAsync(parseResult.GetRequiredValue(query), token);
                if (!json)
                {
                    foreach (var row in result.Results)
                    {
                        Console.Out.WriteLine($"{row.Package}  {row.DisplayName} — {row.Description}");
                    }

                    if (result.Results.Count == 0)
                    {
                        Console.Out.WriteLine($"No package matches '{result.Query}'.");
                    }
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });
        return command;
    }

    private static Command BuildUpdate(Lazy<IServiceProvider> services)
    {
        var command = new Command("update", "Refresh verified repository metadata (root → timestamp → index, D6); never changes installed versions.");
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("update", json, async token =>
            {
                var result = await services.Value.GetRequiredService<UpdateUseCase>().ExecuteAsync(parseResult.GetValue(GlobalOptions.Offline), token);
                if (!json)
                {
                    foreach (var row in result.Repositories)
                    {
                        Console.Out.WriteLine($"{row.Repo}: root v{row.RootVersion}, timestamp v{row.TimestampVersion}, index v{row.IndexVersion}" +
                            (row.IndexChanged ? " (catalog updated)" : " (no changes)") +
                            (row.RotatedRoots.Count > 0 ? $"; root rotated to v{row.RotatedRoots[^1]}" : ""));
                    }

                    if (result.Repositories.Count == 0)
                    {
                        Console.Out.WriteLine("No repository is configured (wbrew repo add <name> <url>).");
                    }
                }

                return result;
            }, ct);
        });
        return command;
    }

    // ---- repo add / list / remove ------------------------------------------------------------

    private static Command BuildRepo(Lazy<IServiceProvider> services)
    {
        var repo = new Command("repo", "Manage configured repositories (D6).");

        var name = new Argument<string>("name") { Description = "Short repository name (A9)." };
        var url = new Argument<string>("url") { Description = "Repository base URL (HTTPS, A13)." };
        var trustRoot = new Option<string[]>("--trust-root")
        {
            Description = "Approve a root key fingerprint (repeatable). Required in non-interactive sessions; never implied by --yes (A8).",
            AllowMultipleArgumentsPerToken = false,
        };
        var add = new Command("add", "Add a repository after approving its root key fingerprints (D6 bootstrap).");
        add.Arguments.Add(name);
        add.Arguments.Add(url);
        add.Options.Add(trustRoot);
        add.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("repo add", json, async token =>
            {
                var sp = services.Value;
                var nonInteractive = await PackageCommands.IsNonInteractiveAsync(sp, parseResult, token);
                var request = new RepoAddRequest(parseResult.GetRequiredValue(name), parseResult.GetRequiredValue(url),
                    parseResult.GetValue(trustRoot) ?? [], nonInteractive);
                var result = await sp.GetRequiredService<RepoAddUseCase>().ExecuteAsync(request, async (repoName, repoUrl, fingerprints, threshold, t) =>
                {
                    Console.Error.WriteLine($"Repository '{repoName}' at {repoUrl} is signed by these root keys (threshold {threshold}):");
                    foreach (var fingerprint in fingerprints)
                    {
                        Console.Error.WriteLine("  " + fingerprint);
                    }

                    Console.Error.WriteLine("Compare them with fingerprints obtained from the publisher through another channel.");
                    return await sp.GetRequiredService<IConsentPrompt>().ConfirmAsync("Trust these root keys?", nonInteractive, t);
                }, token);
                if (!json)
                {
                    Console.Out.WriteLine($"Added '{result.Name}' ({result.Url}): root v{result.RootVersion}, index v{result.IndexVersion}, {result.Packages} package(s).");
                }

                return result;
            }, ct);
        });

        var list = new Command("list", "List configured repositories with their trust state.");
        list.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("repo list", json, async token =>
            {
                var result = await services.Value.GetRequiredService<RepoListUseCase>().ExecuteAsync(token);
                if (!json)
                {
                    foreach (var row in result.Repositories)
                    {
                        Console.Out.WriteLine($"{row.Name}  {row.Url}  root v{row.RootVersion} index v{row.IndexVersion}  {row.Packages} package(s)" +
                            $"  expires {row.Expires}{(row.Expired ? " (EXPIRED — run wbrew update)" : "")}");
                        foreach (var fingerprint in row.RootKeyFingerprints)
                        {
                            Console.Out.WriteLine("    root key " + fingerprint);
                        }
                    }

                    if (result.Repositories.Count == 0)
                    {
                        Console.Out.WriteLine("No repository is configured.");
                    }
                }

                return result;
            }, ct);
        });

        var removeName = new Argument<string>("name") { Description = "Repository name." };
        var remove = new Command("remove", "Remove a repository, its trust state and its catalog.");
        remove.Arguments.Add(removeName);
        remove.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("repo remove", json, async token =>
            {
                var result = await services.Value.GetRequiredService<RepoRemoveUseCase>().ExecuteAsync(parseResult.GetRequiredValue(removeName), token);
                if (!json)
                {
                    Console.Out.WriteLine($"Removed repository '{result.Name}'.");
                }

                return result;
            }, ct);
        });

        repo.Subcommands.Add(add);
        repo.Subcommands.Add(list);
        repo.Subcommands.Add(remove);
        return repo;
    }

    // ---- bundles (A15) -------------------------------------------------------------------------

    private static Command BuildBundle(Lazy<IServiceProvider> services)
    {
        var bundle = new Command("bundle", "Export and install package bundles (A15).");

        var exportFile = new Option<string>("--file") { Description = "Bundle file to write.", Required = true };
        var exportLock = new Option<string?>("--lock") { Description = "Lockfile to write (default: <bundle>.lock.json beside the bundle)." };
        var export = new Command("export", "Write a bundle of the explicitly installed packages and a lockfile of everything installed.");
        export.Options.Add(exportFile);
        export.Options.Add(exportLock);
        export.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("bundle export", json, async token =>
            {
                var result = await services.Value.GetRequiredService<BundleExportUseCase>()
                    .ExecuteAsync(parseResult.GetRequiredValue(exportFile), parseResult.GetValue(exportLock), token);
                if (!json)
                {
                    Console.Out.WriteLine($"Wrote {result.File} ({result.Packages} package(s)) and {result.Lock} ({result.Locked} locked).");
                }

                return result;
            }, ct);
        });

        var installFile = new Option<string>("--file") { Description = "Bundle file to install.", Required = true };
        var installLock = new Option<string?>("--lock") { Description = "Lockfile (default: <bundle>.lock.json beside the bundle)." };
        var locked = new Option<bool>("--locked") { Description = "Install exactly the lockfile; never re-resolve (A15)." };
        var install = new Command("install", "Install a bundle (resolved as one plan) or, with --locked, exactly its lockfile.");
        install.Options.Add(installFile);
        install.Options.Add(installLock);
        install.Options.Add(locked);
        install.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("bundle install", json, async token =>
            {
                var sp = services.Value;
                var nonInteractive = await PackageCommands.IsNonInteractiveAsync(sp, parseResult, token);
                var result = await sp.GetRequiredService<BundleInstallUseCase>().ExecuteAsync(
                    new BundleInstallRequest(parseResult.GetRequiredValue(installFile), parseResult.GetValue(installLock),
                        parseResult.GetValue(locked), parseResult.GetValue(GlobalOptions.Offline)),
                    PackageCommands.Approver(sp, parseResult, nonInteractive, json), token);
                PackageCommands.RenderPlanResult(result.Install, json);
                if (!json && result.LockWritten is not null)
                {
                    Console.Out.WriteLine($"Lockfile written: {result.LockWritten}");
                }

                return new CommandOutput(result, result.Install.Warnings);
            }, ct);
        });

        bundle.Subcommands.Add(export);
        bundle.Subcommands.Add(install);
        return bundle;
    }

    // ---- self update / self uninstall (A14) -----------------------------------------------------

    private static Command BuildSelfUpdate(Lazy<IServiceProvider> services)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Verify the feed and show the available version; change nothing." };
        var command = new Command("update", "Update WinBrew from the signed release feed (A14).");
        command.Options.Add(dryRun);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("self update", json, async token =>
            {
                var sp = services.Value;
                var nonInteractive = await PackageCommands.IsNonInteractiveAsync(sp, parseResult, token);
                var result = await sp.GetRequiredService<SelfUpdateUseCase>().ExecuteAsync(parseResult.GetValue(dryRun), async (current, next, t) =>
                {
                    if (!json)
                    {
                        Console.Out.WriteLine($"Plan: update WinBrew {current} → {next}; every shim switches to the new template (D4).");
                    }

                    return await PackageCommands.ApproveAsync(sp, parseResult, nonInteractive, t);
                }, token);
                if (!json)
                {
                    Console.Out.WriteLine(result.Updated
                        ? $"WinBrew {result.AvailableVersion} installed in {result.AppDirectory}; {result.ShimsReplaced} shim(s) switched."
                        : result.AvailableVersion is { } available
                            ? $"WinBrew {available} is available (running {result.CurrentVersion}); dry run: nothing changed."
                            : $"WinBrew {result.CurrentVersion} is up to date.");
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });
        return command;
    }

    private static Command BuildSelfUninstall(Lazy<IServiceProvider> services)
    {
        var keepData = new Option<bool>("--keep-data") { Description = "Keep packages, cache and state; remove only the PATH entry and shims." };
        var command = new Command("uninstall", "Remove WinBrew (A14).");
        command.Options.Add(keepData);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("self uninstall", json, async token =>
            {
                var sp = services.Value;
                var nonInteractive = await PackageCommands.IsNonInteractiveAsync(sp, parseResult, token);
                var keep = parseResult.GetValue(keepData);
                var result = await sp.GetRequiredService<SelfUninstallUseCase>().ExecuteAsync(keep, async t =>
                {
                    if (!json)
                    {
                        Console.Out.WriteLine(keep
                            ? "Plan: remove the WinBrew PATH entry and every shim; keep packages, cache and state."
                            : "Plan: remove the WinBrew PATH entry, every shim, all packages, the cache, config and state.");
                    }

                    return await PackageCommands.ApproveAsync(sp, parseResult, nonInteractive, t);
                }, token);
                if (!json)
                {
                    Console.Out.WriteLine($"WinBrew removed ({result.ShimsRemoved.Count} shim(s){(result.PathRemoved ? ", PATH entry" : "")}" +
                        $"{(result.DataRemoved ? ", data" : "")}).");
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });
        return command;
    }
}
