using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using WinBrew.Application.Packages;
using WinBrew.Cli.Output;

namespace WinBrew.Cli;

/// <summary>Phase C commands: outdated, pin, unpin, autoremove, cleanup (A8).</summary>
internal static class MaintenanceCommands
{
    public static void Build(RootCommand root, Lazy<IServiceProvider> services)
    {
        root.Subcommands.Add(BuildOutdated(services));
        root.Subcommands.Add(BuildPin(services, pin: true));
        root.Subcommands.Add(BuildPin(services, pin: false));
        root.Subcommands.Add(BuildAutoremove(services));
        root.Subcommands.Add(BuildCleanup(services));
    }

    private static Command BuildOutdated(Lazy<IServiceProvider> services)
    {
        var command = new Command("outdated", "List installed packages with newer catalog versions (read-only).");
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("outdated", json, async token =>
            {
                var result = await services.Value.GetRequiredService<OutdatedUseCase>().ExecuteAsync(token);
                if (!json)
                {
                    if (result.Packages.Count == 0)
                    {
                        Console.Out.WriteLine("Everything is up to date.");
                    }

                    foreach (var row in result.Packages)
                    {
                        Console.Out.WriteLine($"{row.Package} {row.Installed} → {row.Latest}" + (row.Pinned ? " [pinned]" : string.Empty));
                    }
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });
        return command;
    }

    private static Command BuildPin(Lazy<IServiceProvider> services, bool pin)
    {
        var name = pin ? "pin" : "unpin";
        var package = new Argument<string>("package") { Description = "'<id>' or '<repo>/<id>' of an installed package." };
        var command = new Command(name, pin
            ? "Pin an installed package at its current version (D2 rule 1)."
            : "Remove a pin so upgrades may change the package again.");
        command.Arguments.Add(package);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync(name, json, async token =>
            {
                var result = await services.Value.GetRequiredService<PinUseCase>()
                    .ExecuteAsync(parseResult.GetRequiredValue(package), pin, token);
                if (!json)
                {
                    Console.Out.WriteLine(result.Changed
                        ? $"{result.Package} {result.Version} is now {(pin ? "pinned" : "unpinned")}."
                        : $"{result.Package} {result.Version} was already {(pin ? "pinned" : "unpinned")}.");
                }

                return result;
            }, ct);
        });
        return command;
    }

    private static Command BuildAutoremove(Lazy<IServiceProvider> services)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "List what would be removed; change nothing." };
        var command = new Command("autoremove", "Remove dependency-only packages nothing references any more (D8).");
        command.Options.Add(dryRun);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("autoremove", json, async token =>
            {
                var result = await services.Value.GetRequiredService<AutoremoveUseCase>().ExecuteAsync(parseResult.GetValue(dryRun), token);
                if (!json)
                {
                    if (result.Removed.Count == 0)
                    {
                        Console.Out.WriteLine("Nothing to remove.");
                    }

                    foreach (var p in result.Removed)
                    {
                        Console.Out.WriteLine($"{(result.DryRun ? "would remove" : "removed")} {p.Repo}/{p.Id} {p.Version}");
                    }
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });
        return command;
    }

    private static Command BuildCleanup(Lazy<IServiceProvider> services)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "List what would be removed; change nothing." };
        var command = new Command("cleanup", "Trim the artifact cache to cache.maxBytes; never removes active, pinned, referenced or retained artifacts.");
        command.Options.Add(dryRun);
        command.SetAction((parseResult, ct) =>
        {
            var json = parseResult.GetValue(GlobalOptions.Json);
            return CommandRunner.RunAsync("cleanup", json, async token =>
            {
                var result = await services.Value.GetRequiredService<CleanupUseCase>().ExecuteAsync(parseResult.GetValue(dryRun), token);
                if (!json)
                {
                    Console.Out.WriteLine($"{(result.DryRun ? "Would remove" : "Removed")} {result.Removed.Count} cache artifact(s), " +
                        $"{result.FreedBytes} bytes; cache {result.CacheBytesBefore} → {result.CacheBytesAfter} bytes; {result.Protected} protected.");
                }

                return new CommandOutput(result, result.Warnings);
            }, ct);
        });
        return command;
    }
}
