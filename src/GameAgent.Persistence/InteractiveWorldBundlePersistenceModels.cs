using System.Text.Json.Serialization;

namespace GameAgent.Persistence;

internal sealed class InteractiveWorldMemorySidecar
{
    [JsonPropertyName("contract")]
    public string Contract { get; set; } = string.Empty;

    [JsonPropertyName("bindingDigest")]
    public string BindingDigest { get; set; } = string.Empty;

    [JsonPropertyName("records")]
    public List<PersistedMemoryRecord> Records { get; set; } = new();
}

internal sealed class InteractiveWorldGroupSidecar
{
    [JsonPropertyName("contract")]
    public string Contract { get; set; } = string.Empty;

    [JsonPropertyName("bindingDigest")]
    public string BindingDigest { get; set; } = string.Empty;

    [JsonPropertyName("sessions")]
    public List<PersistedGroupInteractionSession> Sessions { get; set; } =
        new();
}

internal sealed class InteractiveWorldPresentationSidecar
{
    [JsonPropertyName("contract")]
    public string Contract { get; set; } = string.Empty;

    [JsonPropertyName("bindingDigest")]
    public string BindingDigest { get; set; } = string.Empty;

    [JsonPropertyName("presentations")]
    public List<PersistedWorldPresentation> Presentations { get; set; } =
        new();
}
