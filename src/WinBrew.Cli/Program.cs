using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using WinBrew.Core;
using WinBrew.Cli.Output;

namespace WinBrew.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Fast path (A4): --version is handled before the command tree, the
        // IHost, and Microsoft.Data.Sqlite are ever touched. --help is served
        // by the parser without any action running, so the lazy container is
        // never built for it either.
        var fastPath = TryRunVersionFastPath(args);
        if (fastPath >= 0)
        {
            return fastPath;
        }

        // Lazy service construction (A4 anchor): parsing never builds the host.
        var services = new Lazy<IServiceProvider>(ServiceConfigurator.BuildProvider);
        var root = CommandTree.Build(services);

        try
        {
            var parseResult = root.Parse(args);

            // Usage errors map to exit 2 through the D9 mapping — SCL's own
            // default exit code (1) must not leak (D9, A4 central mapping).
            if (parseResult.Errors.Count > 0)
            {
                foreach (var error in parseResult.Errors)
                {
                    Console.Error.WriteLine(
                        $"error[{ExitCodes.Name(WinBrewErrorCode.UsageError)}] " +
                        $"({ExitCodes.For(WinBrewErrorCode.UsageError)}): {error.Message}");
                }

                Console.Error.WriteLine("Run 'wbrew --help' for usage.");
                return ExitCodes.For(WinBrewErrorCode.UsageError);
            }

            return await parseResult.InvokeAsync().ConfigureAwait(false);
        }
        catch (WinBrewException ex)
        {
            // Failures raised outside a command handler (e.g., service
            // construction) still go through the central mapping.
            return CommandRunner.Fail("wbrew", json: false, ex.Code, ex.Message, ex.Details);
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.For(WinBrewErrorCode.Cancelled);
        }
        catch (Exception ex)
        {
            return CommandRunner.Fail(
                "wbrew",
                json: false,
                WinBrewErrorCode.GeneralFailure,
                $"Unexpected failure: {ex.Message}",
                null);
        }
        finally
        {
            if (services.IsValueCreated && services.Value is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Handles exactly `wbrew --version` / `wbrew -v` (A4 fast path).</summary>
    private static int TryRunVersionFastPath(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--version" or "-v"))
        {
            return -1;
        }

        var versionInfo = new CliVersionInfo();
        Console.Out.WriteLine($"wbrew {versionInfo.Version} ({versionInfo.RuntimeIdentifier})");
        return ExitCodes.For(WinBrewErrorCode.Success);
    }
}
