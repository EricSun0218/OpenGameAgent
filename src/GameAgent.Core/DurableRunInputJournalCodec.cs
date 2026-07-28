using System.Buffers;
using System.Text.Json;

namespace GameAgent.Core;

internal sealed class DurableRunInputSnapshot
{
    public DurableRunInputSnapshot(
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills)
    {
        Context = context;
        ActiveSkills = activeSkills;
    }

    public IReadOnlyList<ContextCandidate> Context { get; }

    public IReadOnlyList<SkillReference> ActiveSkills { get; }
}

internal static class DurableRunInputJournalCodec
{
    private const int MaxContextCandidates = 512;
    private const int MaxActiveSkills = 128;

    public static JsonElement Encode(
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (activeSkills is null)
        {
            throw new ArgumentNullException(nameof(activeSkills));
        }

        if (context.Count > MaxContextCandidates)
        {
            throw new RuntimeContentLimitException(
                nameof(context),
                "context_candidate_count_exceeded",
                $"Context candidates exceed {MaxContextCandidates}.");
        }

        if (activeSkills.Count > MaxActiveSkills)
        {
            throw new RuntimeContentLimitException(
                nameof(activeSkills),
                "activated_skill_count_exceeded",
                $"Activated skills exceed {MaxActiveSkills}.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("context");
            writer.WriteStartArray();
            foreach (var candidate in context)
            {
                WriteContext(writer, candidate);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("activeSkills");
            writer.WriteStartArray();
            foreach (var skill in activeSkills)
            {
                if (skill is null)
                {
                    throw new ArgumentException(
                        "Active-skill collections cannot contain null entries.",
                        nameof(activeSkills));
                }

                writer.WriteStartObject();
                writer.WriteString("skillId", skill.SkillId);
                writer.WriteString("version", skill.Version);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var result = document.RootElement.Clone();
        JsonValueInspector.ValidateAndMeasure(
            result,
            new JsonValueLimits(),
            "durableRunInput");
        return result;
    }

    public static DurableRunInputSnapshot Decode(JsonElement payload)
    {
        JsonValueInspector.ValidateAndMeasure(
            payload,
            new JsonValueLimits(),
            "durableRunInput");
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("context", out var contextElement)
            || contextElement.ValueKind != JsonValueKind.Array
            || !payload.TryGetProperty(
                "activeSkills",
                out var activeSkillsElement)
            || activeSkillsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "A durable run-input snapshot is malformed.");
        }

        if (contextElement.GetArrayLength() > MaxContextCandidates
            || activeSkillsElement.GetArrayLength() > MaxActiveSkills)
        {
            throw new InvalidDataException(
                "A durable run-input snapshot exceeds its item limits.");
        }

        var context = contextElement
            .EnumerateArray()
            .Select(ReadContext)
            .ToArray();
        var activeSkills = new List<SkillReference>();
        var seenSkills = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in activeSkillsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("skillId", out var skillId)
                || skillId.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "A durable active-skill reference is malformed.");
            }

            var reference = new SkillReference(
                skillId.GetString()!,
                version.GetString()!);
            if (!seenSkills.Add(reference.Value))
            {
                throw new InvalidDataException(
                    "A durable run-input snapshot contains duplicate "
                    + "active skills.");
            }

            activeSkills.Add(reference);
        }

        return new DurableRunInputSnapshot(context, activeSkills);
    }

    private static void WriteContext(
        Utf8JsonWriter writer,
        ContextCandidate candidate)
    {
        if (candidate is null)
        {
            throw new ArgumentException(
                "Context collections cannot contain null entries.",
                nameof(candidate));
        }

        writer.WriteStartObject();
        writer.WriteString("id", candidate.Id);
        writer.WriteString("category", candidate.Category);
        writer.WriteNumber("priority", candidate.Priority);
        writer.WriteBoolean("required", candidate.Required);
        writer.WriteBoolean("canDefer", candidate.CanDefer);
        if (candidate.EstimatedTokens.HasValue)
        {
            writer.WriteNumber(
                "estimatedTokens",
                candidate.EstimatedTokens.Value);
        }

        if (candidate.ExpiresAt.HasValue)
        {
            writer.WriteString("expiresAt", candidate.ExpiresAt.Value);
        }

        if (candidate.Provenance is not null)
        {
            writer.WriteString("provenance", candidate.Provenance);
        }

        if (candidate.Content.HasValue)
        {
            writer.WritePropertyName("content");
            candidate.Content.Value.WriteTo(writer);
        }
        else
        {
            var resource = candidate.Resource
                ?? throw new ArgumentException(
                    "A context candidate requires content or a resource.",
                    nameof(candidate));
            writer.WritePropertyName("resource");
            writer.WriteStartObject();
            writer.WriteString("uri", resource.Uri);
            writer.WriteString("mediaType", resource.MediaType);
            if (resource.Digest is not null)
            {
                writer.WriteString("digest", resource.Digest);
            }

            if (resource.SizeBytes.HasValue)
            {
                writer.WriteNumber("sizeBytes", resource.SizeBytes.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static ContextCandidate ReadContext(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A durable context candidate is malformed.");
        }

        var id = RequiredString(item, "id");
        var category = RequiredString(item, "category");
        var priority = RequiredInt32(item, "priority");
        var required = RequiredBoolean(item, "required");
        var canDefer = RequiredBoolean(item, "canDefer");
        int? estimatedTokens = item.TryGetProperty(
            "estimatedTokens",
            out var estimatedTokensElement)
            ? estimatedTokensElement.GetInt32()
            : null;
        DateTimeOffset? expiresAt = item.TryGetProperty(
            "expiresAt",
            out var expiresAtElement)
            ? expiresAtElement.GetDateTimeOffset()
            : null;
        var provenance = item.TryGetProperty(
            "provenance",
            out var provenanceElement)
            ? provenanceElement.GetString()
            : null;
        var hasContent = item.TryGetProperty("content", out var content);
        var hasResource = item.TryGetProperty("resource", out var resource);
        if (hasContent == hasResource)
        {
            throw new InvalidDataException(
                "A durable context candidate must contain exactly one payload.");
        }

        if (hasContent)
        {
            return new ContextCandidate(
                id,
                category,
                content.Clone(),
                priority,
                required,
                canDefer,
                estimatedTokens,
                expiresAt,
                provenance);
        }

        if (resource.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A durable context resource is malformed.");
        }

        var digest = resource.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString()
            : null;
        long? sizeBytes = resource.TryGetProperty(
            "sizeBytes",
            out var sizeBytesElement)
            ? sizeBytesElement.GetInt64()
            : null;
        return new ContextCandidate(
            id,
            category,
            new ContextResourceReference(
                RequiredString(resource, "uri"),
                RequiredString(resource, "mediaType"),
                digest,
                sizeBytes),
            priority,
            required,
            canDefer,
            estimatedTokens,
            expiresAt,
            provenance);
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)
            || item.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"A durable run-input '{property}' value is malformed.");
        }

        return item.GetString()!;
    }

    private static int RequiredInt32(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)
            || item.ValueKind != JsonValueKind.Number
            || !item.TryGetInt32(out var result))
        {
            throw new InvalidDataException(
                $"A durable run-input '{property}' value is malformed.");
        }

        return result;
    }

    private static bool RequiredBoolean(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)
            || item.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"A durable run-input '{property}' value is malformed.");
        }

        return item.GetBoolean();
    }
}
