namespace WinBrew.Application.Ports;

/// <summary>Effective configuration: schemaVersion plus typed values (D10).</summary>
public sealed record ConfigDocument(string SchemaVersion, IReadOnlyDictionary<string, object?> Values);

/// <summary>One problem found while inspecting config.json (D10).</summary>
public sealed record ConfigProblem(string Key, string Message)
{
    public override string ToString() => $"{Key}: {Message}";
}

/// <summary>
/// Result of a non-throwing config inspection. Used by <c>doctor</c>, which
/// must be able to report a broken config file instead of failing (D10:
/// doctor reports testing.* keys and invalid content).
/// </summary>
public sealed record ConfigInspection(
    ConfigDocument? Document,
    IReadOnlyList<ConfigProblem> Problems);

/// <summary>Configuration persistence port. Implemented over config.json (D10, A6).</summary>
public interface IConfigStore
{
    /// <summary>
    /// Strict load for commands: returns the effective document (defaults
    /// merged). Throws <see cref="WinBrew.Core.WinBrewException"/> on missing
    /// file (with defaults), unreadable JSON, unknown keys, invalid values or
    /// test-hooks-only keys in a release build.
    /// </summary>
    Task<ConfigDocument> LoadAsync(CancellationToken ct);

    /// <summary>Non-throwing inspection for doctor.</summary>
    Task<ConfigInspection> InspectAsync(CancellationToken ct);

    /// <summary>Atomically writes the document (write-to-temp + replace, D4 sidecar rule applied to config).</summary>
    Task SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct);
}
