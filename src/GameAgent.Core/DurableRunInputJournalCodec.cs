using System.Buffers;
using System.Text.Json;

namespace GameAgent.Core;

internal sealed class DurableRunInputSnapshot
{
    public DurableRunInputSnapshot(
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills,
        string workloadClass)
    {
        Context = context;
        ActiveSkills = activeSkills;
        WorkloadClass = workloadClass;
    }

    public IReadOnlyList<ContextCandidate> Context { get; }

    public IReadOnlyList<SkillReference> ActiveSkills { get; }

    public string WorkloadClass { get; }
}

internal static class DurableRunInputJournalCodec
{
    internal const int MaxContextCandidates = 512;
    internal const int MaxActiveSkills = 128;
    private const int MaxEncodedUtf8Bytes = 262_144;
    private static readonly JsonValueLimits JournalJsonLimits = new(
        maxUtf8Bytes: MaxEncodedUtf8Bytes);

    public static JsonElement Encode(
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills)
    {
        return Encode(
            context,
            activeSkills,
            ProviderWorkloadClasses.Interactive);
    }

    public static JsonElement Encode(
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills,
        string workloadClass)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (activeSkills is null)
        {
            throw new ArgumentNullException(nameof(activeSkills));
        }

        workloadClass = ProviderWorkloadClasses.Normalize(
            workloadClass,
            nameof(workloadClass));
        var contextSnapshot = RuntimeInputGuard.CopyBounded(
            context,
            MaxContextCandidates,
            candidate => candidate
                         ?? throw new ArgumentException(
                             "Context collections cannot contain null entries.",
                             nameof(context)),
            nameof(context),
            "context_candidate_count_exceeded");
        var activeSkillSnapshot = RuntimeInputGuard.CopyBounded(
            activeSkills,
            MaxActiveSkills,
            skill => skill
                     ?? throw new ArgumentException(
                         "Active-skill collections cannot contain null entries.",
                         nameof(activeSkills)),
            nameof(activeSkills),
            "activated_skill_count_exceeded");
        ValidateUniqueActiveSkills(activeSkillSnapshot);
        using var buffer = new BoundedBufferWriter(
            MaxEncodedUtf8Bytes,
            nameof(context),
            "durable_run_input_bytes_exceeded");
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WritePayload(
                writer,
                contextSnapshot,
                activeSkillSnapshot,
                workloadClass);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var result = document.RootElement.Clone();
        JsonValueInspector.ValidateAndMeasure(
            result,
            JournalJsonLimits,
            "durableRunInput");
        return result;
    }

    internal static void ValidateEncodedSize(
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills)
    {
        _ = Encode(context, activeSkills);
    }

    internal static void ValidateEncodedSize(
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills,
        string workloadClass)
    {
        _ = Encode(context, activeSkills, workloadClass);
    }

    internal static void ValidateUniqueActiveSkills(
        IReadOnlyList<SkillReference> activeSkills)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in activeSkills)
        {
            if (skill is null || !seen.Add(skill.Value))
            {
                throw new ArgumentException(
                    "Active-skill collections cannot contain duplicates "
                    + "or null entries.",
                    nameof(activeSkills));
            }
        }
    }

    private static void WritePayload(
        Utf8JsonWriter writer,
        IReadOnlyList<ContextCandidate> context,
        IReadOnlyList<SkillReference> activeSkills,
        string workloadClass)
    {
        writer.WriteStartObject();
        writer.WriteString("workloadClass", workloadClass);
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

        var workloadClass = payload.TryGetProperty(
                "workloadClass",
                out var workloadClassElement)
            ? ReadWorkloadClass(workloadClassElement)
            : ProviderWorkloadClasses.Interactive;
        return new DurableRunInputSnapshot(
            context,
            activeSkills,
            workloadClass);
    }

    private static string ReadWorkloadClass(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "A durable provider workload class is malformed.");
        }

        try
        {
            return ProviderWorkloadClasses.Normalize(
                value.GetString(),
                "workloadClass");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A durable provider workload class is not supported.",
                exception);
        }
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

        if (candidate.ObservationAdmissionMetadata is not null)
        {
            WriteObservationAdmission(
                writer,
                candidate.ObservationAdmissionMetadata);
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
        var observationAdmission = item.TryGetProperty(
                "observationAdmission",
                out var observationAdmissionElement)
            ? ReadObservationAdmission(observationAdmissionElement)
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
                null,
                priority,
                required,
                canDefer,
                estimatedTokens,
                expiresAt,
                provenance,
                observationAdmission);
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
            null,
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
            provenance,
            observationAdmission);
    }

    private static void WriteObservationAdmission(
        Utf8JsonWriter writer,
        ObservationAdmissionSnapshot admission)
    {
        writer.WritePropertyName("observationAdmission");
        writer.WriteStartObject();
        writer.WriteString("observationId", admission.ObservationId);
        writer.WriteString("worldId", admission.WorldId);
        if (admission.SessionId is not null)
        {
            writer.WriteString("sessionId", admission.SessionId);
        }

        writer.WriteString("scope", admission.Scope);
        writer.WritePropertyName("audienceIds");
        writer.WriteStartArray();
        foreach (var audienceId in admission.AudienceIds)
        {
            writer.WriteStringValue(audienceId);
        }

        writer.WriteEndArray();
        writer.WriteString(
            "bindingState",
            admission.BindingState switch
            {
                AudienceIncarnationBindingState.Missing => "missing",
                AudienceIncarnationBindingState.Invalid => "invalid",
                AudienceIncarnationBindingState.Valid => "valid",
                _ => throw new InvalidOperationException(
                    "Audience incarnation binding state is unsupported.")
            });
        writer.WritePropertyName("bindings");
        writer.WriteStartArray();
        foreach (var binding in admission.Bindings)
        {
            writer.WriteStartObject();
            writer.WriteString("audienceId", binding.AudienceId);
            writer.WriteString("entityId", binding.Entity.EntityId);
            writer.WriteNumber(
                "incarnation",
                binding.Entity.Incarnation);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static ObservationAdmissionSnapshot ReadObservationAdmission(
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("audienceIds", out var audienceIds)
            || audienceIds.ValueKind != JsonValueKind.Array
            || audienceIds.GetArrayLength()
            > ObservationAudienceIncarnations.MaxBindings
            || !value.TryGetProperty("bindingState", out var bindingState)
            || bindingState.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("bindings", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array
            || bindings.GetArrayLength()
            > ObservationAudienceIncarnations.MaxBindings)
        {
            throw new InvalidDataException(
                "A durable observation-admission snapshot is malformed.");
        }

        var state = bindingState.GetString() switch
        {
            "missing" => AudienceIncarnationBindingState.Missing,
            "invalid" => AudienceIncarnationBindingState.Invalid,
            "valid" => AudienceIncarnationBindingState.Valid,
            _ => throw new InvalidDataException(
                "A durable audience incarnation state is malformed.")
        };
        var audience = audienceIds
            .EnumerateArray()
            .Select(ReadStringValue)
            .ToArray();
        var parsedBindings =
            new List<ObservationAudienceIncarnationBinding>(
                bindings.GetArrayLength());
        foreach (var binding in bindings.EnumerateArray())
        {
            if (binding.ValueKind != JsonValueKind.Object
                || !binding.TryGetProperty(
                    "incarnation",
                    out var incarnation)
                || incarnation.ValueKind != JsonValueKind.Number
                || !incarnation.TryGetInt64(out var incarnationValue))
            {
                throw new InvalidDataException(
                    "A durable audience incarnation binding is malformed.");
            }

            try
            {
                parsedBindings.Add(
                    new ObservationAudienceIncarnationBinding(
                        RequiredString(binding, "audienceId"),
                        new GameEntityIdentity(
                            RequiredString(binding, "entityId"),
                            incarnationValue)));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "A durable audience incarnation binding is malformed.",
                    exception);
            }
        }

        try
        {
            return new ObservationAdmissionSnapshot(
                RequiredString(value, "observationId"),
                RequiredString(value, "worldId"),
                value.TryGetProperty("sessionId", out var sessionId)
                    ? ReadStringValue(sessionId)
                    : null,
                RequiredString(value, "scope"),
                audience,
                state,
                parsedBindings);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A durable observation-admission snapshot is malformed.",
                exception);
        }
    }

    private static string ReadStringValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "A durable run-input string value is malformed.");
        }

        return value.GetString()!;
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

    private sealed class BoundedBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private readonly int _maximumBytes;
        private readonly string _parameterName;
        private readonly string _limitCode;
        private byte[]? _buffer;
        private int _written;

        public BoundedBufferWriter(
            int maximumBytes,
            string parameterName,
            string limitCode)
        {
            _maximumBytes = maximumBytes;
            _parameterName = parameterName;
            _limitCode = limitCode;
            _buffer = ArrayPool<byte>.Shared.Rent(maximumBytes);
        }

        public ReadOnlyMemory<byte> WrittenMemory =>
            Buffer.AsMemory(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || count > _maximumBytes - _written)
            {
                ThrowLimit();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            sizeHint = NormalizeSizeHint(sizeHint);
            if (sizeHint > _maximumBytes - _written)
            {
                ThrowLimit();
            }

            return Buffer.AsMemory(
                _written,
                _maximumBytes - _written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            sizeHint = NormalizeSizeHint(sizeHint);
            if (sizeHint > _maximumBytes - _written)
            {
                ThrowLimit();
            }

            return Buffer.AsSpan(
                _written,
                _maximumBytes - _written);
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private byte[] Buffer =>
            _buffer ?? throw new ObjectDisposedException(
                nameof(BoundedBufferWriter));

        private static int NormalizeSizeHint(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            return sizeHint == 0 ? 1 : sizeHint;
        }

        private void ThrowLimit()
        {
            throw new RuntimeContentLimitException(
                _parameterName,
                _limitCode,
                $"The bounded writer cannot exceed "
                + $"{_maximumBytes} UTF-8 bytes.");
        }
    }
}
