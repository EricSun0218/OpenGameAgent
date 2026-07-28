using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GodotArray = global::Godot.Collections.Array;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

public static class GodotProtocolVariantMapper
{
    public static DurableRunRequest ToDurableRunRequest(
        GodotDictionary run,
        GodotArray observations)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (observations is null)
        {
            throw new ArgumentNullException(nameof(observations));
        }

        var context = new List<ContextCandidate>(observations.Count);
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = ProtocolJson.DeserializeObservationEnvelope(
                StringifyObject(observations[index], $"observations[{index}]"));
            context.Add(
                ContextCandidate.FromObservation(
                    observation,
                    required: true,
                    canDefer: false));
        }

        return new DurableRunRequest
        {
            Run = ProtocolJson.DeserializeAgentRun(
                NormalizeJsonNumbers(global::Godot.Json.Stringify(run))),
            Context = context
        };
    }

    public static HeadlessRunRequest ToRunRequest(
        GodotDictionary run,
        GodotArray observations,
        GodotArray tools)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (observations is null)
        {
            throw new ArgumentNullException(nameof(observations));
        }

        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        var mappedObservations = new List<ObservationEnvelope>(observations.Count);
        for (var index = 0; index < observations.Count; index++)
        {
            mappedObservations.Add(
                ProtocolJson.DeserializeObservationEnvelope(
                    StringifyObject(observations[index], $"observations[{index}]")));
        }

        var mappedTools = new List<ToolDescriptor>(tools.Count);
        for (var index = 0; index < tools.Count; index++)
        {
            mappedTools.Add(
                ProtocolJson.DeserializeToolDescriptor(
                    StringifyObject(tools[index], $"tools[{index}]")));
        }

        return new HeadlessRunRequest
        {
            Run = ProtocolJson.DeserializeAgentRun(
                NormalizeJsonNumbers(global::Godot.Json.Stringify(run))),
            Observations = mappedObservations,
            Tools = mappedTools
        };
    }

    public static ObservationEnvelope ToObservation(
        GodotDictionary observation)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        return ProtocolJson.DeserializeObservationEnvelope(
            NormalizeJsonNumbers(
                global::Godot.Json.Stringify(observation)));
    }

    public static GodotDictionary ToDictionary(AgentRun value) =>
        ToDictionary(ProtocolJson.ToElement(value));

    public static GodotDictionary ToDictionary(RuntimeEvent value) =>
        ToDictionary(ProtocolJson.ToElement(value));

    public static GodotDictionary ToDictionary(ObservationEnvelope value) =>
        ToDictionary(ProtocolJson.ToElement(value));

    public static GodotDictionary ToDictionary(ToolDescriptor value) =>
        ToDictionary(ProtocolJson.ToElement(value));

    public static GodotArray ToArray(IEnumerable<ObservationEnvelope> values)
    {
        var result = new GodotArray();
        foreach (var value in values)
        {
            result.Add(ToDictionary(value));
        }

        return result;
    }

    public static GodotArray ToArray(IEnumerable<ToolDescriptor> values)
    {
        var result = new GodotArray();
        foreach (var value in values)
        {
            result.Add(ToDictionary(value));
        }

        return result;
    }

    internal static GodotDictionary ParseDictionary(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a JSON object.");
        }

        return ToDictionary(document.RootElement);
    }

    internal static global::Godot.Variant ParseVariant(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        using var document = JsonDocument.Parse(json);
        return ToVariant(document.RootElement);
    }

    private static string StringifyObject(
        global::Godot.Variant value,
        string path)
    {
        if (value.VariantType != global::Godot.Variant.Type.Dictionary)
        {
            throw new JsonException($"{path} must be a Dictionary.");
        }

        return NormalizeJsonNumbers(global::Godot.Json.Stringify(value));
    }

    private static string NormalizeJsonNumbers(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNormalized(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNormalized(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalized(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteNormalized(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                {
                    if (element.TryGetInt64(out var exactInteger))
                    {
                        writer.WriteNumberValue(exactInteger);
                        break;
                    }

                    var number = element.GetDouble();
                    if (double.IsFinite(number)
                        && number >= long.MinValue
                        && number <= long.MaxValue
                        && number == Math.Truncate(number))
                    {
                        writer.WriteNumberValue((long)number);
                    }
                    else
                    {
                        writer.WriteNumberValue(number);
                    }

                    break;
                }
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException(
                    $"Unsupported JSON token '{element.ValueKind}'.");
        }
    }

    private static GodotDictionary ToDictionary(JsonElement element)
    {
        var result = new GodotDictionary();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ToVariant(property.Value);
        }

        return result;
    }

    private static global::Godot.Variant ToVariant(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return ToDictionary(element);
            case JsonValueKind.Array:
                {
                    var array = new GodotArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        array.Add(ToVariant(item));
                    }

                    return array;
                }
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    return integer;
                }

                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return default;
            default:
                throw new JsonException(
                    $"Unsupported JSON token '{element.ValueKind}'.");
        }
    }
}
