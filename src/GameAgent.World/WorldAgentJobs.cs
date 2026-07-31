using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public enum WorldAgentJobKind
{
    Understanding = 0,
    Selection = 1,
    Narration = 2
}

public enum WorldAgentFailurePolicy
{
    PauseForInput = 0,
    UseFallback = 1,
    Skip = 2,
    Fault = 3
}

/// <summary>
/// Binds an authoritative agent decision to one immutable world draft.
/// The draft digest covers the complete predeclared option-to-effect mapping;
/// it is never derived from model output.
/// </summary>
public sealed class WorldAgentAuthoritativeBinding
{
    public WorldAgentAuthoritativeBinding(
        string draftId,
        string draftDigest,
        string occurrenceId,
        WorldAuthoritativeCoordinate expectedCoordinate)
    {
        DraftId = WorldValidation.Required(
            draftId,
            nameof(draftId),
            192);
        if (!CanonicalJsonDigest.IsSha256(draftDigest))
        {
            throw new ArgumentException(
                "Draft digest must be a lowercase SHA-256 digest.",
                nameof(draftDigest));
        }

        DraftDigest = draftDigest;
        OccurrenceId = WorldValidation.Required(
            occurrenceId,
            nameof(occurrenceId),
            192);
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
    }

    public string DraftId { get; }

    public string DraftDigest { get; }

    public string OccurrenceId { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    internal void EnsureCompatible(
        string occurrenceId,
        string catalogDigest,
        GameContextCoordinate coordinate)
    {
        if (!string.Equals(
                OccurrenceId,
                occurrenceId,
                StringComparison.Ordinal)
            || !string.Equals(
                ExpectedCoordinate.CatalogDigest,
                catalogDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                ExpectedCoordinate.WorldId,
                coordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                ExpectedCoordinate.TimelineId,
                coordinate.TimelineId,
                StringComparison.Ordinal)
            || ExpectedCoordinate.SaveRevision != coordinate.SaveRevision
            || (coordinate.GameTime is not null
                && coordinate.GameTime.Epoch
                != ExpectedCoordinate.TimelineEpoch)
            || !long.TryParse(
                coordinate.StateVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var stateVersion)
            || stateVersion != ExpectedCoordinate.StateVersion
            || !string.Equals(
                coordinate.StateVersion,
                stateVersion.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The authoritative binding must exactly match the job.",
                nameof(coordinate));
        }
    }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("draftId", DraftId);
        writer.WriteString("draftDigest", DraftDigest);
        writer.WriteString("occurrenceId", OccurrenceId);
        writer.WritePropertyName("expectedCoordinate");
        writer.WriteStartObject();
        writer.WriteString("worldId", ExpectedCoordinate.WorldId);
        writer.WriteString("timelineId", ExpectedCoordinate.TimelineId);
        writer.WriteString(
            "timelineEpoch",
            ExpectedCoordinate.TimelineEpoch.ToString(
                CultureInfo.InvariantCulture));
        writer.WriteString(
            "saveRevision",
            ExpectedCoordinate.SaveRevision.ToString(
                CultureInfo.InvariantCulture));
        writer.WriteString(
            "stateVersion",
            ExpectedCoordinate.StateVersion.ToString(
                CultureInfo.InvariantCulture));
        writer.WriteString(
            "catalogDigest",
            ExpectedCoordinate.CatalogDigest);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

/// <summary>
/// One bounded language job associated with an immutable world coordinate.
/// Its result remains a proposal until the authoritative world transaction
/// validates and commits it.
/// </summary>
public sealed class WorldAgentJob
{
    private static readonly JsonValueLimits JobJsonLimits = new(
        maxUtf8Bytes: 262_144,
        maxDepth: 48,
        maxNodes: 16_384,
        maxStringUtf8Bytes: 65_536,
        maxContainerItems: 4_096);

    private readonly JsonElement _input;
    private readonly JsonElement _outputSchema;
    private readonly JsonElement? _fallbackOutput;
    private readonly JsonElement _envelope;

    public WorldAgentJob(
        string jobId,
        string runId,
        string agentId,
        string occurrenceId,
        WorldAgentJobKind kind,
        GameContextCoordinate coordinate,
        JsonElement input,
        string outputSchemaId,
        string outputSchemaVersion,
        JsonElement outputSchema,
        WorldAgentFailurePolicy failurePolicy,
        string catalogDigest,
        string? batchId = null,
        JsonElement? fallbackOutput = null,
        WorldAgentAuthoritativeBinding? authoritativeBinding = null)
    {
        JobId = WorldValidation.Required(jobId, nameof(jobId), 128);
        RunId = WorldValidation.Required(runId, nameof(runId), 128);
        AgentId = WorldValidation.Required(agentId, nameof(agentId), 128);
        OccurrenceId = WorldValidation.Required(
            occurrenceId,
            nameof(occurrenceId),
            192);
        if (!Enum.IsDefined(typeof(WorldAgentJobKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(
                typeof(WorldAgentFailurePolicy),
                failurePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(failurePolicy));
        }

        Coordinate = coordinate
                     ?? throw new ArgumentNullException(nameof(coordinate));
        if (coordinate.StateVersion is null)
        {
            throw new ArgumentException(
                "An agent job requires an exact state version.",
                nameof(coordinate));
        }

        JsonValueInspector.ValidateAndMeasure(
            input,
            JobJsonLimits,
            nameof(input));
        _input = input.Clone();
        OutputSchemaId = WorldValidation.Required(
            outputSchemaId,
            nameof(outputSchemaId),
            128);
        OutputSchemaVersion = WorldValidation.Required(
            outputSchemaVersion,
            nameof(outputSchemaVersion),
            64);
        JsonValueInspector.ValidateAndMeasure(
            outputSchema,
            JobJsonLimits,
            nameof(outputSchema));
        _ = new FinalOutputContract(
            OutputSchemaId,
            OutputSchemaVersion,
            outputSchema);
        _outputSchema = outputSchema.Clone();
        if (!CanonicalJsonDigest.IsSha256(catalogDigest))
        {
            throw new ArgumentException(
                "Catalog digest must be lowercase SHA-256.",
                nameof(catalogDigest));
        }

        if (fallbackOutput.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                fallbackOutput.Value,
                JobJsonLimits,
                nameof(fallbackOutput));
            var validation = new ToolArgumentValidator().Validate(
                _outputSchema,
                fallbackOutput.Value);
            if (!validation.IsValid)
            {
                throw new ArgumentException(
                    "Fallback output does not satisfy the output schema.",
                    nameof(fallbackOutput));
            }

            _fallbackOutput = fallbackOutput.Value.Clone();
        }

        if (failurePolicy == WorldAgentFailurePolicy.UseFallback
            && !_fallbackOutput.HasValue)
        {
            throw new ArgumentException(
                "Fallback policy requires a schema-valid fallback output.",
                nameof(fallbackOutput));
        }

        Kind = kind;
        FailurePolicy = failurePolicy;
        CatalogDigest = catalogDigest;
        BatchId = WorldValidation.Optional(
            batchId,
            nameof(batchId),
            128);
        if (authoritativeBinding is not null)
        {
            if (kind == WorldAgentJobKind.Narration)
            {
                throw new ArgumentException(
                    "Narration cannot carry an authoritative draft binding.",
                    nameof(authoritativeBinding));
            }

            authoritativeBinding.EnsureCompatible(
                OccurrenceId,
                CatalogDigest,
                Coordinate);
        }

        AuthoritativeBinding = authoritativeBinding;
        _envelope = WriteEnvelope();
        SemanticDigest =
            CanonicalJsonDigest.ComputeSha256(_envelope);
    }

    public string JobId { get; }

    public string RunId { get; }

    public string AgentId { get; }

    public string OccurrenceId { get; }

    public string? BatchId { get; }

    public WorldAgentJobKind Kind { get; }

    public GameContextCoordinate Coordinate { get; }

    public JsonElement Input => _input.Clone();

    public string OutputSchemaId { get; }

    public string OutputSchemaVersion { get; }

    public JsonElement OutputSchema => _outputSchema.Clone();

    public WorldAgentFailurePolicy FailurePolicy { get; }

    public JsonElement? FallbackOutput => _fallbackOutput?.Clone();

    public string CatalogDigest { get; }

    public WorldAgentAuthoritativeBinding? AuthoritativeBinding { get; }

    public string SemanticDigest { get; }

    public bool IsAuthoritativeOutput =>
        Kind is WorldAgentJobKind.Understanding
            or WorldAgentJobKind.Selection;

    public JsonElement ToEnvelope()
    {
        return _envelope.Clone();
    }

    public void EnsureCurrentCoordinate(GameContextCoordinate current)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (!string.Equals(
                Coordinate.WorldId,
                current.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                Coordinate.TimelineId,
                current.TimelineId,
                StringComparison.Ordinal)
            || Coordinate.SaveRevision != current.SaveRevision
            || !string.Equals(
                Coordinate.StateVersion,
                current.StateVersion,
                StringComparison.Ordinal)
            || !SameEntity(Coordinate.Observer, current.Observer))
        {
            throw new InvalidOperationException(
                "World agent job coordinate is stale.");
        }
    }

    private JsonElement WriteEnvelope()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", "game-agent.world-agent-job.v1");
            writer.WriteString("jobId", JobId);
            writer.WriteString("runId", RunId);
            writer.WriteString("agentId", AgentId);
            writer.WriteString("occurrenceId", OccurrenceId);
            if (BatchId is not null)
            {
                writer.WriteString("batchId", BatchId);
            }

            writer.WriteString("kind", Kind.ToString());
            writer.WriteString(
                "failurePolicy",
                FailurePolicy.ToString());
            writer.WriteString("catalogDigest", CatalogDigest);
            writer.WritePropertyName("coordinate");
            GameContextEnvelope.ToJson(Coordinate).WriteTo(writer);
            if (AuthoritativeBinding is not null)
            {
                writer.WritePropertyName("authoritativeBinding");
                AuthoritativeBinding.WriteTo(writer);
            }

            writer.WritePropertyName("input");
            _input.WriteTo(writer);
            writer.WriteString("outputSchemaId", OutputSchemaId);
            writer.WriteString(
                "outputSchemaVersion",
                OutputSchemaVersion);
            writer.WriteString(
                "outputSchemaDigest",
                CanonicalJsonDigest.ComputeSha256(_outputSchema));
            writer.WritePropertyName("fallbackOutputDigest");
            if (_fallbackOutput.HasValue)
            {
                writer.WriteStringValue(
                    CanonicalJsonDigest.ComputeSha256(
                        _fallbackOutput.Value));
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private static bool SameEntity(
        GameEntityIdentity? left,
        GameEntityIdentity? right)
    {
        return left is null
            ? right is null
            : left.IsSameIncarnation(right);
    }
}

public static class WorldAgentOutputSchemas
{
    public static JsonElement Selection(IEnumerable<string> optionIds)
    {
        if (optionIds is null)
        {
            throw new ArgumentNullException(nameof(optionIds));
        }

        var collected = new List<string>(256);
        foreach (var option in optionIds)
        {
            if (collected.Count >= 256)
            {
                throw new ArgumentException(
                    "Selection options must be a bounded unique set.",
                    nameof(optionIds));
            }

            collected.Add(
                WorldValidation.Required(
                    option,
                    nameof(optionIds),
                    192));
        }

        var options = collected
            .OrderBy(option => option, StringComparer.Ordinal)
            .ToArray();
        if (options.Length < 1
            || options.Distinct(StringComparer.Ordinal).Count()
            != options.Length)
        {
            throw new ArgumentException(
                "Selection options must be a bounded unique set.",
                nameof(optionIds));
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteBoolean("additionalProperties", false);
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            writer.WritePropertyName("optionId");
            writer.WriteStartObject();
            writer.WriteString("type", "string");
            writer.WritePropertyName("enum");
            writer.WriteStartArray();
            foreach (var option in options)
            {
                writer.WriteStringValue(option);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            writer.WriteStringValue("optionId");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    public static JsonElement Narration(int maximumCharacters = 16_384)
    {
        if (maximumCharacters is < 1 or > 262_144)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters));
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteBoolean("additionalProperties", false);
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            writer.WritePropertyName("text");
            writer.WriteStartObject();
            writer.WriteString("type", "string");
            writer.WriteNumber("maxLength", maximumCharacters);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            writer.WriteStringValue("text");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }
}
