using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Cli;

/// <summary>
/// Console yes/no prompt (A8). In non-interactive mode nothing is ever
/// prompted — missing consent exits with 10 (ConsentRequired). The prompt
/// itself goes to stderr; stdout stays reserved for data and the JSON
/// envelope.
/// </summary>
public sealed class ConsoleConsentPrompt : IConsentPrompt
{
    public Task<bool> ConfirmAsync(string prompt, bool nonInteractive, CancellationToken ct)
    {
        if (nonInteractive)
        {
            throw new WinBrewException(
                WinBrewErrorCode.ConsentRequired,
                $"Consent required but the session is non-interactive: {prompt} " +
                "(no prompt is shown in this mode; pass the explicit flag or run interactively, A8).");
        }

        Console.Error.Write($"{prompt} [y/N] ");
        Console.Error.Flush();

        var answer = Console.ReadLine();
        if (answer is null)
        {
            // EOF — treat as refusal, never as consent.
            return Task.FromResult(false);
        }

        var normalized = answer.Trim().ToLowerInvariant();
        return Task.FromResult(normalized is "y" or "yes");
    }
}
