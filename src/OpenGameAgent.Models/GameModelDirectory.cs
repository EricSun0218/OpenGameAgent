using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;

namespace OpenGameAgent.Models;

public sealed class GameModelDirectorySnapshot
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<GameModelDescriptor>> _modelsByProvider;

    internal GameModelDirectorySnapshot(
        string version,
        DateTimeOffset generatedAt,
        IReadOnlyList<GameProviderDescriptor> providers,
        IReadOnlyList<GameModelDescriptor> models)
    {
        Version = version;
        GeneratedAt = generatedAt;
        Providers = Array.AsReadOnly(providers.ToArray());
        Models = Array.AsReadOnly(models.ToArray());
        _modelsByProvider = new ReadOnlyDictionary<string, IReadOnlyList<GameModelDescriptor>>(
            Models.GroupBy(model => model.ProviderId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<GameModelDescriptor>)Array.AsReadOnly(
                        group.OrderBy(model => model.ModelId, StringComparer.Ordinal).ToArray()),
                    StringComparer.Ordinal));
    }

    public string Version { get; }

    public DateTimeOffset GeneratedAt { get; }

    public IReadOnlyList<GameProviderDescriptor> Providers { get; }

    public IReadOnlyList<GameModelDescriptor> Models { get; }

    public IReadOnlyList<GameModelDescriptor> GetModels(string providerId)
    {
        var id = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        return _modelsByProvider.TryGetValue(id, out var models)
            ? models
            : Array.Empty<GameModelDescriptor>();
    }

    public GameProviderDescriptor? GetProvider(string providerId)
    {
        var id = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        return Providers.FirstOrDefault(provider => string.Equals(provider.ProviderId, id, StringComparison.Ordinal));
    }
}

public static class GameModelDirectory
{
    private const string ResourceName = "OpenGameAgent.Models.Data.model-directory.json";
    private const int MaximumJsonCharacters = 20_000_000;
    private const int MaximumProviders = 512;
    private const int MaximumModels = 100_000;
    private static readonly Lazy<GameModelDirectorySnapshot> Bundled =
        new(LoadBundledCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public static GameModelDirectorySnapshot LoadBundled() => Bundled.Value;

    private static GameModelDirectorySnapshot LoadBundledCore()
    {
        using var stream = typeof(GameModelDirectory).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The bundled model directory resource is unavailable.");
        using var reader = new StreamReader(stream);
        return ParseJson(reader.ReadToEnd());
    }

    public static GameModelDirectorySnapshot ParseJson(string json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        if (json.Length is < 2 or > MaximumJsonCharacters)
        {
            throw new ArgumentException("The model directory JSON is outside its allowed size.", nameof(json));
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            return ParseRoot(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The model directory JSON is invalid.", nameof(json), exception);
        }
    }

    private static GameModelDirectorySnapshot ParseRoot(JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object, "root");
        var version = RequiredString(root, "version", 128);
        var generatedAtText = RequiredString(root, "generatedAt", 128);
        if (!DateTimeOffset.TryParse(
                generatedAtText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var generatedAt))
        {
            throw new ArgumentException("The model directory generation time is invalid.");
        }

        if (!root.TryGetProperty("providers", out var providersElement))
        {
            throw new ArgumentException("The model directory omitted its providers.");
        }

        RequireKind(providersElement, JsonValueKind.Array, "providers");
        if (providersElement.GetArrayLength() > MaximumProviders)
        {
            throw new ArgumentException("The model directory contains too many providers.");
        }

        var providers = new List<GameProviderDescriptor>();
        var models = new List<GameModelDescriptor>();
        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var providerElement in providersElement.EnumerateArray())
        {
            RequireKind(providerElement, JsonValueKind.Object, "provider");
            var providerId = RequiredString(providerElement, "id", 256);
            if (!providerIds.Add(providerId))
            {
                throw new ArgumentException($"The model directory contains duplicate provider '{providerId}'.");
            }

            var endpointText = OptionalString(providerElement, "endpoint", 4096);
            var provider = new GameProviderDescriptor(
                providerId,
                OptionalString(providerElement, "name", 4096) ?? providerId,
                endpointText is null ? null : new Uri(endpointText, UriKind.Absolute),
                OptionalBoolean(providerElement, "local") ?? false,
                supportsDynamicModels: false,
                ParseStringMap(providerElement, "metadata"));
            providers.Add(provider);

            if (!providerElement.TryGetProperty("models", out var modelsElement))
            {
                throw new ArgumentException($"Provider '{providerId}' omitted its models.");
            }

            RequireKind(modelsElement, JsonValueKind.Array, "models");
            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var modelElement in modelsElement.EnumerateArray())
            {
                if (models.Count >= MaximumModels)
                {
                    throw new ArgumentException("The model directory contains too many models.");
                }

                var model = ParseModel(provider, modelElement);
                if (!modelIds.Add(model.ModelId))
                {
                    throw new ArgumentException(
                        $"Provider '{providerId}' contains duplicate model '{model.ModelId}'.");
                }

                models.Add(model);
            }
        }

        return new GameModelDirectorySnapshot(version, generatedAt, providers, models);
    }

    private static GameModelDescriptor ParseModel(GameProviderDescriptor provider, JsonElement element)
    {
        RequireKind(element, JsonValueKind.Object, "model");
        var modelBaseUrl = OptionalString(element, "baseUrl", 4096);
        var reasoningLevels = ParseReasoningLevels(element);
        var outputCapabilities = ParseOutputCapabilities(element);
        if (reasoningLevels.Any(level => level != GameReasoningLevel.Off))
        {
            outputCapabilities |= GameModelOutputCapabilities.Reasoning;
        }

        var modelId = RequiredString(element, "id", 1024);
        return new GameModelDescriptor(
            provider.ProviderId,
            modelId,
            OptionalString(element, "name", 4096),
            OptionalInt32(element, "contextWindow") ?? 0,
            OptionalInt32(element, "maximumOutput") ?? 0,
            ParseInputCapabilities(element),
            outputCapabilities,
            reasoningLevels,
            ParseCost(element, modelId),
            ParseStringMap(element, "metadata"),
            ParseReasoningValues(element, reasoningLevels),
            OptionalString(element, "api", 256) ?? "custom",
            modelBaseUrl is null ? provider.Endpoint : new Uri(modelBaseUrl, UriKind.Absolute),
            OptionalObjectJson(element, "sampling"),
            ParseHeaderMap(element, "headers"),
            OptionalObjectJson(element, "compatibility"));
    }

    private static GameModelInputCapabilities ParseInputCapabilities(JsonElement element)
    {
        var result = GameModelInputCapabilities.None;
        foreach (var value in ParseStringArray(element, "input"))
        {
            result |= value switch
            {
                "text" => GameModelInputCapabilities.Text,
                "image" => GameModelInputCapabilities.Image,
                "audio" => GameModelInputCapabilities.Audio,
                "video" => GameModelInputCapabilities.Video,
                "structured" => GameModelInputCapabilities.StructuredData,
                _ => throw new ArgumentException($"Unknown model input capability '{value}'."),
            };
        }

        return result;
    }

    private static GameModelOutputCapabilities ParseOutputCapabilities(JsonElement element)
    {
        var result = GameModelOutputCapabilities.None;
        foreach (var value in ParseStringArray(element, "output"))
        {
            result |= value switch
            {
                "text" => GameModelOutputCapabilities.Text,
                "image" => GameModelOutputCapabilities.Image,
                "audio" => GameModelOutputCapabilities.Audio,
                "video" => GameModelOutputCapabilities.Video,
                "structured" => GameModelOutputCapabilities.StructuredData,
                "tools" => GameModelOutputCapabilities.ToolCalls,
                "reasoning" => GameModelOutputCapabilities.Reasoning,
                _ => throw new ArgumentException($"Unknown model output capability '{value}'."),
            };
        }

        return result;
    }

    private static IReadOnlyList<GameReasoningLevel> ParseReasoningLevels(JsonElement element)
    {
        var values = ParseStringArray(element, "reasoning");
        return values.Select(value => value switch
        {
            "off" => GameReasoningLevel.Off,
            "minimal" => GameReasoningLevel.Minimal,
            "low" => GameReasoningLevel.Low,
            "medium" => GameReasoningLevel.Medium,
            "high" => GameReasoningLevel.High,
            "xhigh" => GameReasoningLevel.ExtraHigh,
            "max" => GameReasoningLevel.Maximum,
            _ => throw new ArgumentException($"Unknown reasoning level '{value}'."),
        }).ToArray();
    }

    private static IReadOnlyDictionary<GameReasoningLevel, string> ParseReasoningValues(
        JsonElement element,
        IReadOnlyCollection<GameReasoningLevel> levels)
    {
        if (!element.TryGetProperty("reasoningValues", out var values))
        {
            return new Dictionary<GameReasoningLevel, string>();
        }

        RequireKind(values, JsonValueKind.Object, "reasoningValues");
        var result = new Dictionary<GameReasoningLevel, string>();
        foreach (var property in values.EnumerateObject())
        {
            var level = property.Name switch
            {
                "off" => GameReasoningLevel.Off,
                "minimal" => GameReasoningLevel.Minimal,
                "low" => GameReasoningLevel.Low,
                "medium" => GameReasoningLevel.Medium,
                "high" => GameReasoningLevel.High,
                "xhigh" => GameReasoningLevel.ExtraHigh,
                "max" => GameReasoningLevel.Maximum,
                _ => throw new ArgumentException($"Unknown reasoning-value level '{property.Name}'."),
            };
            if (!levels.Contains(level) || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("A reasoning value targets an unsupported level.");
            }

            result[level] = property.Value.GetString()!;
        }

        return new ReadOnlyDictionary<GameReasoningLevel, string>(result);
    }

    private static GameModelCost ParseCost(JsonElement element, string modelId)
    {
        if (!element.TryGetProperty("cost", out var cost))
        {
            return new GameModelCost();
        }

        RequireKind(cost, JsonValueKind.Object, "cost");
        var tiers = new List<GameModelCostTier>();
        if (cost.TryGetProperty("tiers", out var tiersElement))
        {
            RequireKind(tiersElement, JsonValueKind.Array, "tiers");
            foreach (var tier in tiersElement.EnumerateArray())
            {
                tiers.Add(new GameModelCostTier(
                    RequiredInt64(tier, "above"),
                    OptionalDecimal(tier, "input") ?? 0,
                    OptionalDecimal(tier, "output") ?? 0,
                    OptionalDecimal(tier, "cacheRead") ?? 0,
                    OptionalDecimal(tier, "cacheWrite") ?? 0));
            }
        }

        var input = OptionalDecimal(cost, "input") ?? 0;
        var output = OptionalDecimal(cost, "output") ?? 0;
        var cacheRead = OptionalDecimal(cost, "cacheRead") ?? 0;
        var cacheWrite = OptionalDecimal(cost, "cacheWrite") ?? 0;
        var known = OptionalBoolean(cost, "known")
            ?? input != 0
            || output != 0
            || cacheRead != 0
            || cacheWrite != 0
            || tiers.Count != 0
            || modelId.EndsWith(":free", StringComparison.OrdinalIgnoreCase);
        return new GameModelCost(
            input,
            output,
            cacheRead,
            cacheWrite,
            tiers,
            known);
    }

    private static IReadOnlyDictionary<string, string> ParseStringMap(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var map))
        {
            return new Dictionary<string, string>();
        }

        RequireKind(map, JsonValueKind.Object, propertyName);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"'{propertyName}' values must be strings.");
            }

            result.Add(property.Name, property.Value.GetString()!);
        }

        return new ReadOnlyDictionary<string, string>(result);
    }

    private static IReadOnlyDictionary<string, string?> ParseHeaderMap(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var map))
        {
            return new Dictionary<string, string?>();
        }

        RequireKind(map, JsonValueKind.Object, propertyName);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new ArgumentException($"'{propertyName}' values must be strings or null.");
            }

            result.Add(
                property.Name,
                property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString());
        }

        return new ReadOnlyDictionary<string, string?>(result);
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array))
        {
            return Array.Empty<string>();
        }

        RequireKind(array, JsonValueKind.Array, propertyName);
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"'{propertyName}' entries must be strings.");
            }

            values.Add(item.GetString()!);
        }

        return values;
    }

    private static string? OptionalObjectJson(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        RequireKind(value, JsonValueKind.Object, propertyName);
        return value.GetRawText();
    }

    private static string RequiredString(JsonElement element, string name, int maximum) =>
        OptionalString(element, name, maximum)
        ?? throw new ArgumentException($"The model directory omitted '{name}'.");

    private static string? OptionalString(JsonElement element, string name, int maximum)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"The model directory field '{name}' must be a string.");
        }

        var result = value.GetString();
        return string.IsNullOrWhiteSpace(result) || result.Length > maximum
            ? throw new ArgumentException($"The model directory field '{name}' is invalid.")
            : result;
    }

    private static bool? OptionalBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"The model directory field '{name}' must be a boolean."),
        };
    }

    private static int? OptionalInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) && result >= 0
            ? result
            : throw new ArgumentException($"The model directory field '{name}' must be a non-negative integer.");
    }

    private static long RequiredInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result)
        && result >= 0
            ? result
            : throw new ArgumentException($"The model directory field '{name}' must be a non-negative integer.");

    private static decimal? OptionalDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result) && result >= 0
            ? result
            : throw new ArgumentException($"The model directory field '{name}' must be a non-negative number.");
    }

    private static void RequireKind(JsonElement element, JsonValueKind kind, string name)
    {
        if (element.ValueKind != kind)
        {
            throw new ArgumentException($"The model directory field '{name}' must be {kind}.");
        }
    }
}
