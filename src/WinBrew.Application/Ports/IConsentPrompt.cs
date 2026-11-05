using WinBrew.Core;

namespace WinBrew.Application.Ports;

/// <summary>Inputs that decide interactivity (A8).</summary>
public sealed record InteractivityOptions(
    bool NonInteractiveFlag,
    string? ConfigInteractive,
    string? WinBrewNonInteractiveEnv,
    bool StdinIsTerminal)
{
    /// <summary>
    /// Non-interactive when --non-interactive is passed, interactive=never is
    /// configured, WINBREW_NONINTERACTIVE=1 is set, or stdin is not a
    /// terminal (A8).
    /// </summary>
    public bool IsNonInteractive =>
        NonInteractiveFlag ||
        ConfigInteractive == "never" ||
        WinBrewNonInteractiveEnv == "1" ||
        !StdinIsTerminal;
}

/// <summary>Yes/no consent prompt port. Implemented by the CLI console.</summary>
public interface IConsentPrompt
{
    /// <summary>
    /// Prompts and returns the answer. Implementations throw
    /// <see cref="WinBrewException"/> (ConsentRequired, exit 10) when the
    /// session is non-interactive — nothing is prompted in that mode (A8).
    /// </summary>
    Task<bool> ConfirmAsync(string prompt, bool nonInteractive, CancellationToken ct);
}
