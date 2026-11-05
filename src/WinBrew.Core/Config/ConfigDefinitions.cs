namespace WinBrew.Core.Config;

/// <summary>
/// Value types of configuration keys (D10). The CA1720 type-name rule is
/// suppressed here on purpose: Int/Long ARE the domain terms — they describe
/// the JSON value shape of D10 keys, not .NET types.
/// </summary>
#pragma warning disable CA1720
public enum ConfigValueType
{
    Text,
    Enum,
    Integer,
    Long,
    Bool,
}
#pragma warning restore CA1720

/// <summary>Definition of one configuration key (D10 table).</summary>
public sealed record ConfigKeyDefinition
{
    public required string Key { get; init; }
    public required ConfigValueType Type { get; init; }
    public required object Default { get; init; }

    /// <summary>Allowed values for <see cref="ConfigValueType.Enum"/> keys.</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>Inclusive minimum for numeric keys; null = unbounded below.</summary>
    public long? Min { get; init; }

    /// <summary>Inclusive maximum for numeric keys; null = unbounded above.</summary>
    public long? Max { get; init; }

    /// <summary>
    /// True for <c>testing.*</c> keys: honored only in test-hooks builds
    /// (-p:WinBrewTestHooks=true). Release builds reject them in
    /// <c>config set</c> with exit 2 and <c>doctor</c> reports them when
    /// present in config.json (D10).
    /// </summary>
    public bool TestHooksOnly { get; init; }

    public string Notes { get; init; } = string.Empty;
}

/// <summary>
/// The registry of every configuration key (D10). One place; the JSON
/// schema (config.v1.schema.json) and this registry must agree — schema
/// conformance tests assert that.
/// </summary>
public static class ConfigDefinitions
{
    public static readonly IReadOnlyDictionary<string, ConfigKeyDefinition> Keys =
        new Dictionary<string, ConfigKeyDefinition>(StringComparer.Ordinal)
        {
            ["color"] = new()
            {
                Key = "color", Type = ConfigValueType.Enum, Default = "auto",
                AllowedValues = ["auto", "always", "never"],
                Notes = "--no-color and NO_COLOR override it",
            },
            ["interactive"] = new()
            {
                Key = "interactive", Type = ConfigValueType.Enum, Default = "auto",
                AllowedValues = ["auto", "never"],
                Notes = "'never' behaves like --non-interactive",
            },
            ["cache.maxBytes"] = new()
            {
                Key = "cache.maxBytes", Type = ConfigValueType.Long, Default = 5368709120L,
                Min = 0, Notes = "Cache budget for cleanup",
            },
            ["download.timeoutSeconds"] = new()
            {
                Key = "download.timeoutSeconds", Type = ConfigValueType.Integer, Default = 300,
                Min = 1, Max = 86400, Notes = "Per artifact",
            },
            ["download.maxBytes"] = new()
            {
                Key = "download.maxBytes", Type = ConfigValueType.Long, Default = 4294967296L,
                Min = 0, Notes = "Hard limit per artifact",
            },
            ["download.retries"] = new()
            {
                Key = "download.retries", Type = ConfigValueType.Integer, Default = 3,
                Min = 0, Max = 5, Notes = "Exponential backoff with jitter",
            },
            ["retainVersions"] = new()
            {
                Key = "retainVersions", Type = ConfigValueType.Integer, Default = 1,
                Min = 0, Max = 5, Notes = "Inactive portable versions kept for recovery",
            },
            ["lock.timeoutSeconds"] = new()
            {
                Key = "lock.timeoutSeconds", Type = ConfigValueType.Integer, Default = 30,
                Min = 1, Max = 3600, Notes = string.Empty,
            },
            ["trust.maxClockSkewSeconds"] = new()
            {
                Key = "trust.maxClockSkewSeconds", Type = ConfigValueType.Integer, Default = 300,
                Min = 0, Max = 86400, Notes = string.Empty,
            },
            ["trust.maxMetadataBytes"] = new()
            {
                Key = "trust.maxMetadataBytes", Type = ConfigValueType.Long, Default = 67108864L,
                Min = 0, Notes = "Max size of any metadata file (e.g. index.json)",
            },
            ["archive.maxEntries"] = new()
            {
                Key = "archive.maxEntries", Type = ConfigValueType.Integer, Default = 100000,
                Min = 1, Notes = string.Empty,
            },
            ["archive.maxTotalBytes"] = new()
            {
                Key = "archive.maxTotalBytes", Type = ConfigValueType.Long, Default = 8589934592L,
                Min = 0, Notes = string.Empty,
            },
            ["archive.maxCompressionRatio"] = new()
            {
                Key = "archive.maxCompressionRatio", Type = ConfigValueType.Integer, Default = 200,
                Min = 1, Notes = "Checked per entry and in total",
            },
            ["path.managed"] = new()
            {
                Key = "path.managed", Type = ConfigValueType.Bool, Default = false,
                Notes = "Set to true only after the user consents",
            },
            ["testing.allowInsecureLocalhost"] = new()
            {
                Key = "testing.allowInsecureLocalhost", Type = ConfigValueType.Bool, Default = false,
                TestHooksOnly = true,
                Notes = "Honored only in test-hooks builds with WINBREW_TEST_MODE=1; " +
                        "release builds reject the key and doctor reports it (D10)",
            },
        };

    /// <summary>Default configuration snapshot: every key at its default value.</summary>
    public static IReadOnlyDictionary<string, object?> Defaults() =>
        Keys.ToDictionary(k => k.Key, k => (object?)k.Value.Default, StringComparer.Ordinal);

    /// <summary>
    /// Validates a raw string value for a key and returns the typed value.
    /// Throws <see cref="WinBrewException"/> (UsageError, exit 2) on unknown
    /// keys, wrong types, out-of-range numbers and disallowed enum values.
    /// Test-hooks-only keys are rejected unless the build defines
    /// WINBREW_TEST_HOOKS (D10).
    /// </summary>
    public static object ValidateAndParse(string key, string rawValue, bool testHooksBuild)
    {
        if (!Keys.TryGetValue(key, out var def))
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, $"Unknown configuration key '{key}'.");
        }

        if (def.TestHooksOnly && !testHooksBuild)
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Configuration key '{key}' is only valid in test-hooks builds " +
                "(-p:WinBrewTestHooks=true); release builds reject it (D10).");
        }

        try
        {
            return def.Type switch
            {
                ConfigValueType.Bool => ParseBool(key, rawValue),
                ConfigValueType.Integer => (int)CheckRange(def, int.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture)),
                ConfigValueType.Long => CheckRange(def, long.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture)),
                ConfigValueType.Enum => ParseEnum(def, rawValue),
                ConfigValueType.Text => rawValue,
                _ => throw new InvalidOperationException($"Unhandled {def.Type}"),
            };
        }
        catch (FormatException)
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Configuration key '{key}' expects {def.Type}, got '{rawValue}'.");
        }
        catch (OverflowException)
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Configuration value '{rawValue}' for '{key}' is out of range.");
        }
    }

    /// <summary>
    /// Validates a deserialized value from config.json (typed by the JSON
    /// reader). Same rules as <see cref="ValidateAndParse"/>.
    /// </summary>
    public static object ValidateTyped(string key, object? value, bool testHooksBuild)
    {
        if (!Keys.TryGetValue(key, out var def))
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, $"Unknown configuration key '{key}'.");
        }

        if (def.TestHooksOnly && !testHooksBuild)
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Configuration key '{key}' is only valid in test-hooks builds; " +
                "release builds reject it (D10).");
        }

        return (def.Type, value) switch
        {
            (ConfigValueType.Bool, bool b) => b,
            (ConfigValueType.Integer, int i) => (int)CheckRange(def, i),
            (ConfigValueType.Integer, long l) => (int)CheckRange(def, l),
            (ConfigValueType.Long, int i) => CheckRange(def, i),
            (ConfigValueType.Long, long l) => CheckRange(def, l),
            (ConfigValueType.Enum, string s) => ParseEnum(def, s),
            (ConfigValueType.Text, string s) => s,
            _ => throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Configuration key '{key}' expects {def.Type}, got '{value}'."),
        };
    }

    private static bool ParseBool(string key, string rawValue) => rawValue.ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => throw new WinBrewException(
            WinBrewErrorCode.UsageError,
            $"Configuration key '{key}' expects true or false, got '{rawValue}'."),
    };

    private static long ParseNumber(long value, ConfigKeyDefinition def) => CheckRange(def, value);

    private static long CheckRange(ConfigKeyDefinition def, long value)
    {
        if ((def.Min.HasValue && value < def.Min.Value) || (def.Max.HasValue && value > def.Max.Value))
        {
            var range = def.Max.HasValue ? $"{def.Min}..{def.Max}" : $">= {def.Min}";
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Configuration key '{def.Key}' must be {range}, got {value}.");
        }

        return value;
    }

    private static string ParseEnum(ConfigKeyDefinition def, string rawValue)
    {
        var allowed = def.AllowedValues ?? throw new InvalidOperationException($"Key {def.Key} is not an enum");
        if (allowed.Contains(rawValue, StringComparer.Ordinal))
        {
            return rawValue;
        }

        throw new WinBrewException(
            WinBrewErrorCode.UsageError,
            $"Configuration key '{def.Key}' must be one of: {string.Join(", ", allowed)}. Got '{rawValue}'.");
    }
}
