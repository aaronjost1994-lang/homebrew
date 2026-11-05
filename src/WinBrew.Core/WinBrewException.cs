namespace WinBrew.Core;

/// <summary>
/// The one exception type that crosses the use-case boundary. Handlers never
/// map exceptions per call site — the CLI maps <see cref="WinBrewException"/>
/// centrally to exit codes and the JSON error envelope (A4/D9).
/// </summary>
public sealed class WinBrewException : Exception
{
    public WinBrewErrorCode Code { get; }

    /// <summary>Structured details for the JSON error envelope ("details" object, A8).</summary>
    public IReadOnlyDictionary<string, object?> Details { get; }

    public WinBrewException(
        WinBrewErrorCode code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        Details = details ?? new Dictionary<string, object?>();
    }
}
