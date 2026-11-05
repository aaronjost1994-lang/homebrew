using System.Text.Json;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Config;

namespace WinBrew.Infrastructure.Config;

/// <summary>config.json on-disk document (D10): schemaVersion + values object.</summary>
public sealed record ConfigFileModel(string SchemaVersion, Dictionary<string, JsonElement>? Values);

/// <summary>
/// config.json persistence (D10). Strict load merges defaults and validates
/// every key; inspection reports problems without throwing so doctor can
/// render them. Saves are atomic: write-to-temp then replace.
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    public const string SchemaVersion = "1";

    private readonly string _path;
    private readonly bool _testHooksBuild;

    public JsonConfigStore(string configFilePath, bool testHooksBuild)
    {
        _path = configFilePath;
        _testHooksBuild = testHooksBuild;
    }

    public async Task<ConfigDocument> LoadAsync(CancellationToken ct)
    {
        var (file, problems) = await ReadAndValidateAsync(ct).ConfigureAwait(false);

        if (problems.Count > 0)
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                "config.json is invalid: " + string.Join("; ", problems.Select(p => p.Message)),
                new Dictionary<string, object?>
                {
                    ["problems"] = problems.Select(p => new Dictionary<string, object?>
                    {
                        ["key"] = p.Key,
                        ["message"] = p.Message,
                    }).ToList(),
                });
        }

        var values = file?.Values is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : await ConvertTypedAsync(file.Values).ConfigureAwait(false);

        return new ConfigDocument(SchemaVersion, values);
    }

    public async Task<ConfigInspection> InspectAsync(CancellationToken ct)
    {
        var (file, problems) = await ReadAndValidateAsync(ct).ConfigureAwait(false);

        ConfigDocument? document = null;
        if (problems.Count == 0 && file is not null)
        {
            var values = await ConvertTypedAsync(file.Values ?? []).ConfigureAwait(false);
            document = new ConfigDocument(SchemaVersion, values);
        }

        return new ConfigInspection(document, problems);
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        var jsonValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            jsonValues[key] = ToJsonElement(key, value);
        }

        var model = new ConfigFileModel(SchemaVersion, jsonValues);
        var json = JsonSerializer.Serialize(
            model,
            InfrastructureJsonContext.Default.ConfigFileModel);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
        File.Move(tempPath, _path, overwrite: true);
    }

    // ---- internals -------------------------------------------------------

    private async Task<(ConfigFileModel? File, IReadOnlyList<ConfigProblem> Problems)> ReadAndValidateAsync(CancellationToken ct)
    {
        var problems = new List<ConfigProblem>();

        if (!File.Exists(_path))
        {
            return (null, problems);
        }

        ConfigFileModel? file;
        try
        {
            var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            file = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ConfigFileModel);
        }
        catch (JsonException ex)
        {
            return (null, [new ConfigProblem("(file)", $"config.json is not valid JSON: {ex.Message}")]);
        }

        if (file is null)
        {
            return (null, [new ConfigProblem("(file)", "config.json is empty or not an object.")]);
        }

        if (file.SchemaVersion != SchemaVersion)
        {
            problems.Add(new ConfigProblem("schemaVersion",
                $"Expected \"{SchemaVersion}\", got \"{file.SchemaVersion}\"."));
        }

        if (file.Values is null)
        {
            return (file, problems);
        }

        foreach (var key in file.Values.Keys)
        {
            if (!ConfigDefinitions.Keys.TryGetValue(key, out var def))
            {
                problems.Add(new ConfigProblem(key, $"Unknown configuration key '{key}' (D10)."));
            }
            else if (def.TestHooksOnly && !_testHooksBuild)
            {
                problems.Add(new ConfigProblem(key,
                    $"'{key}' is a testing.* key — honored only in test-hooks builds; " +
                    "release builds reject it (D10/A13)."));
            }
        }

        return (file, problems);
    }

    private async Task<Dictionary<string, object?>> ConvertTypedAsync(Dictionary<string, JsonElement> raw)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, element) in raw)
        {
            if (!ConfigDefinitions.Keys.TryGetValue(key, out var def))
            {
                continue; // already reported by validation
            }

            if (def.TestHooksOnly && !_testHooksBuild)
            {
                continue; // already reported by validation; skip silently here
            }

            values[key] = ValidateElement(key, element);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return values;
    }

    private object ValidateElement(string key, JsonElement element)
    {
        // Coerce JSON numbers to the declared D10 width first so callers see
        // consistent types regardless of how the file spelled the number.
        if (!ConfigDefinitions.Keys.TryGetValue(key, out var def))
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, $"Unknown configuration key '{key}'.");
        }

        object raw = element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when def.Type == ConfigValueType.Long => element.GetInt64(),
            JsonValueKind.Number => element.GetInt32(),
            JsonValueKind.String => element.GetString() ?? throw new WinBrewException(
                WinBrewErrorCode.UsageError, $"Configuration key '{key}' must not be null."),
            _ => throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Configuration key '{key}' has an unsupported JSON type ({element.ValueKind})."),
        };

        return ConfigDefinitions.ValidateTyped(key, raw, _testHooksBuild);
    }

    private static JsonElement ToJsonElement(string key, object? value) => value switch
    {
        null => throw new WinBrewException(WinBrewErrorCode.UsageError, $"Key '{key}' cannot be null."),
        bool b => JsonSerializer.SerializeToElement(b, InfrastructureJsonContext.Default.Boolean),
        int i => JsonSerializer.SerializeToElement(i, InfrastructureJsonContext.Default.Int32),
        long l => JsonSerializer.SerializeToElement(l, InfrastructureJsonContext.Default.Int64),
        string s => JsonSerializer.SerializeToElement(s, InfrastructureJsonContext.Default.String),
        _ => throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"Unsupported value type for '{key}'."),
    };
}
