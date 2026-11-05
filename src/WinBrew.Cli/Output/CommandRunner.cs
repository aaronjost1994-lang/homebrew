using System.Text.Json;
using WinBrew.Core;

namespace WinBrew.Cli.Output;

/// <summary>
/// Central command execution: runs the use case, renders the JSON envelope
/// (A8) or lets the command render text itself, and maps every exception
/// through <see cref="ExitCodes"/> — never per handler (A4/D9).
/// </summary>
/// <summary>A command result with warnings for the envelope's "warnings" array (A8, A10 fallback warnings).</summary>
internal sealed record CommandOutput(object? Data, IReadOnlyList<string> Warnings);

internal static class CommandRunner
{
    /// <summary>
    /// Runs <paramref name="work"/>. The work returns the JSON data payload
    /// (or null); text rendering happens inside the work itself when --json
    /// is not set.
    /// </summary>
    public static async Task<int> RunAsync(
        string commandName,
        bool json,
        Func<CancellationToken, Task<object?>> work,
        CancellationToken ct)
    {
        try
        {
            var data = await work(ct).ConfigureAwait(false);
            IReadOnlyList<string> warnings = [];
            if (data is CommandOutput output)
            {
                data = output.Data;
                warnings = output.Warnings;
            }

            if (json)
            {
                WriteEnvelope(JsonEnvelope.Success(commandName, data, warnings));
            }
            else
            {
                // Diagnostics go to stderr; stdout stays reserved for results (A8).
                foreach (var warning in warnings)
                {
                    Console.Error.WriteLine($"warning: {warning}");
                }
            }

            return ExitCodes.For(WinBrewErrorCode.Success);
        }
        catch (WinBrewException ex)
        {
            return Fail(commandName, json, ex.Code, ex.Message, ex.Details);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail(commandName, json, WinBrewErrorCode.Cancelled, "Cancelled (Ctrl+C).", null);
        }
        catch (OperationCanceledException ex)
        {
            return Fail(commandName, json, WinBrewErrorCode.Cancelled, $"Cancelled: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return Fail(commandName, json, WinBrewErrorCode.GeneralFailure,
                $"Unexpected failure: {ex.Message}", null);
        }
    }

    /// <summary>Maps a thrown exception from outside the per-command runner (parsing-level failures).</summary>
    public static int Fail(string commandName, bool json, WinBrewErrorCode code, string message,
        IReadOnlyDictionary<string, object?>? details)
    {
        if (json)
        {
            WriteEnvelope(JsonEnvelope.Failure(
                commandName,
                ExitCodes.For(code),
                ExitCodes.Name(code),
                message,
                details));
        }
        else
        {
            Console.Error.WriteLine($"error[{ExitCodes.Name(code)}] ({ExitCodes.For(code)}): {message}");
        }

        return ExitCodes.For(code);
    }

    private static void WriteEnvelope(JsonEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope, CliJsonContext.Default.JsonEnvelope);
        Console.Out.WriteLine(json);
    }
}
