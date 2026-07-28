using System.Text.Json.Serialization;
using GameAgent.Protocol;

namespace GameAgent.Persistence;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(JournalFrameRecord))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext
{
}

internal sealed class JournalFrameRecord
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("streamId")]
    public string StreamId { get; set; } = string.Empty;

    [JsonPropertyName("runSequence")]
    public long RunSequence { get; set; }

    [JsonPropertyName("runRevision")]
    public long RunRevision { get; set; }

    [JsonPropertyName("runtimeEvent")]
    public RuntimeEvent? RuntimeEvent { get; set; }

    [JsonPropertyName("runtimeEvents")]
    public List<RuntimeEvent>? RuntimeEvents { get; set; }
}
