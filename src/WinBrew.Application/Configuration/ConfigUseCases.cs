using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Config;

namespace WinBrew.Application.Configuration;

/// <summary>wbrew config get &lt;key&gt; — returns the effective value (default when unset).</summary>
public sealed class GetConfigValueUseCase(IConfigStore store)
{
    /// <summary>Throws NotFound (exit 3) for unknown keys (D9).</summary>
    public async Task<(string Key, string Value, bool IsDefault)> ExecuteAsync(string key, CancellationToken ct)
    {
        if (!ConfigDefinitions.Keys.ContainsKey(key))
        {
            throw new WinBrewException(WinBrewErrorCode.NotFound, $"Unknown configuration key '{key}'.");
        }

        var doc = await store.LoadAsync(ct);
        var hasValue = doc.Values.TryGetValue(key, out var value) && value is not null;
        var effective = hasValue ? value : ConfigDefinitions.Keys[key].Default;

        return (key, ConfigValueFormatter.FormatValue(effective), IsDefault: !hasValue);
    }
}

/// <summary>wbrew config set &lt;key&gt; &lt;value&gt; — validates, then persists (D10).</summary>
public sealed class SetConfigValueUseCase(IConfigStore store)
{
    /// <summary>
    /// Validates the raw value (UsageError/exit 2 on unknown keys, bad types,
    /// out-of-range values, testing.* keys in release builds) and saves the
    /// merged document.
    /// </summary>
    public async Task<(string Key, string Value)> ExecuteAsync(string key, string rawValue, bool testHooksBuild, CancellationToken ct)
    {
        var typed = ConfigDefinitions.ValidateAndParse(key, rawValue, testHooksBuild);

        var doc = await store.LoadAsync(ct);
        var values = new Dictionary<string, object?>(doc.Values, StringComparer.Ordinal)
        {
            [key] = typed,
        };

        await store.SaveAsync(values, ct);
        return (key, ConfigValueFormatter.FormatValue(typed));
    }
}

/// <summary>wbrew config list — every key with its effective value and source.</summary>
public sealed class ListConfigUseCase(IConfigStore store)
{
    public async Task<IReadOnlyList<(string Key, string Value, bool IsDefault)>> ExecuteAsync(CancellationToken ct)
    {
        var doc = await store.LoadAsync(ct);

        var rows = new List<(string Key, string Value, bool IsDefault)>();
        foreach (var (key, def) in ConfigDefinitions.Keys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var hasValue = doc.Values.TryGetValue(key, out var value) && value is not null;
            var effective = hasValue ? value : def.Default;
            rows.Add((key, ConfigValueFormatter.FormatValue(effective), !hasValue));
        }

        return rows;
    }
}

internal static class ConfigValueFormatter
{
    public static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
