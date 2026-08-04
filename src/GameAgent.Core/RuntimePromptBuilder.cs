using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

internal static class RuntimePromptBuilder
{
    public static PromptMeasurement MeasurePrompt(
        IReadOnlyList<NormalizedMessage> transcript,
        IReadOnlyList<ToolDescriptor> tools,
        int maxMessages,
        int maxUtf8Bytes,
        int estimatedBytesPerToken,
        IRuntimeTokenEstimator? tokenEstimator = null)
    {
        if (transcript is null)
        {
            throw new ArgumentNullException(nameof(transcript));
        }

        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        if (transcript.Count > maxMessages)
        {
            throw new RuntimeContentLimitException(
                nameof(transcript),
                "prompt_message_count_exceeded",
                $"Prompt transcript exceeds {maxMessages} messages.");
        }

        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        long utf8Bytes = 2;
        long estimatedTokens = 0;
        foreach (var message in transcript)
        {
            if (message is null)
            {
                throw new ArgumentException(
                    "Prompt transcripts cannot contain null messages.",
                    nameof(transcript));
            }

            if (!messageIds.Add(message.MessageId))
            {
                throw new ArgumentException(
                    $"Prompt message id '{message.MessageId}' is duplicated.",
                    nameof(transcript));
            }

            PreflightMessage(message, maxUtf8Bytes);
            var encoded = NormalizedMessageJournalCodec.Encode(message);
            _ = NormalizedMessageJournalCodec.Decode(encoded);
            var raw = encoded.GetRawText();
            utf8Bytes = checked(
                utf8Bytes + Encoding.UTF8.GetByteCount(raw));
            if (tokenEstimator is not null)
            {
                estimatedTokens = checked(
                    estimatedTokens
                    + tokenEstimator.EstimateTokens(raw));
            }
            EnsurePromptBytes(utf8Bytes, maxUtf8Bytes, nameof(transcript));
        }

        foreach (var tool in tools)
        {
            if (tool is null)
            {
                throw new ArgumentException(
                    "Prompt tool catalogs cannot contain null tools.",
                    nameof(tools));
            }

            var encoded = ProtocolJson.ToElement(tool);
            var raw = encoded.GetRawText();
            utf8Bytes = checked(
                utf8Bytes + Encoding.UTF8.GetByteCount(raw));
            if (tokenEstimator is not null)
            {
                estimatedTokens = checked(
                    estimatedTokens
                    + tokenEstimator.EstimateTokens(raw));
            }
            EnsurePromptBytes(utf8Bytes, maxUtf8Bytes, nameof(tools));
        }

        var measuredTokens = tokenEstimator is null
            ? Math.Max(
                1,
                checked(
                    (int)((utf8Bytes + estimatedBytesPerToken - 1)
                          / estimatedBytesPerToken)))
            : checked((int)Math.Max(1, estimatedTokens));
        return new PromptMeasurement(
            (int)utf8Bytes,
            measuredTokens,
            tokenEstimator?.EstimatorId ?? "utf8-ratio",
            tokenEstimator?.Version ?? "1");
    }

    private static void PreflightMessage(
        NormalizedMessage message,
        int maximumUtf8Bytes)
    {
        if (message.Parts is null || message.Parts.Count is < 1 or > 2_048)
        {
            throw new RuntimeContentLimitException(
                nameof(message),
                "prompt_part_count_exceeded",
                "A prompt message has an invalid number of parts.");
        }

        long bytes = 128;
        Add(message.MessageId);
        Add(message.Role);
        var jsonLimits = new JsonValueLimits(
            maxUtf8Bytes: maximumUtf8Bytes,
            maxStringUtf8Bytes: Math.Min(maximumUtf8Bytes, 262_144));
        foreach (var part in message.Parts)
        {
            if (part is null)
            {
                throw new ArgumentException(
                    "Prompt message parts cannot contain null entries.",
                    nameof(message));
            }

            Add(part.Type);
            Add(part.Text);
            Add(part.ToolCallId);
            Add(part.ToolName);
            Add(part.ToolVersion);
            Add(part.ToolEffect);
            Add(part.ToolDescriptorDigest);
            if (part.Json.HasValue)
            {
                bytes += JsonValueInspector.ValidateAndMeasure(
                    part.Json.Value,
                    jsonLimits,
                    nameof(message));
            }
            EnsureBounded();
        }

        void Add(string? value)
        {
            if (value is not null)
            {
                bytes += Encoding.UTF8.GetByteCount(value);
                EnsureBounded();
            }
        }

        void EnsureBounded()
        {
            if (bytes > maximumUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(message),
                    "prompt_bytes_exceeded",
                    "A prompt message exceeds the prompt byte limit.");
            }
        }
    }

    public static JsonElement PromptMeasurementEvidence(
        PromptMeasurement measurement)
    {
        if (measurement is null)
        {
            throw new ArgumentNullException(nameof(measurement));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("utf8Bytes", measurement.Utf8Bytes);
            writer.WriteNumber(
                "estimatedTokens",
                measurement.EstimatedTokens);
            writer.WriteString(
                "estimatorId",
                measurement.EstimatorId);
            writer.WriteString(
                "estimatorVersion",
                measurement.EstimatorVersion);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static NormalizedMessage ContextMessage(
        string messageId,
        CompiledContext context,
        DateTimeOffset createdAt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.context+json");
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in context.Selected)
            {
                var candidate = item.Candidate;
                writer.WriteStartObject();
                writer.WriteString("id", candidate.Id);
                writer.WriteString("category", candidate.Category);
                writer.WriteNumber("priority", candidate.Priority);
                writer.WriteBoolean("required", candidate.Required);
                if (candidate.Provenance is not null)
                {
                    writer.WriteString("provenance", candidate.Provenance);
                }

                if (candidate.Content.HasValue)
                {
                    writer.WritePropertyName("content");
                    candidate.Content.Value.WriteTo(writer);
                }
                else if (candidate.Resource is not null)
                {
                    writer.WritePropertyName("resource");
                    writer.WriteStartObject();
                    writer.WriteString("uri", candidate.Resource.Uri);
                    writer.WriteString(
                        "mediaType",
                        candidate.Resource.MediaType);
                    if (candidate.Resource.Digest is not null)
                    {
                        writer.WriteString("digest", candidate.Resource.Digest);
                    }

                    if (candidate.Resource.SizeBytes.HasValue)
                    {
                        writer.WriteNumber(
                            "sizeBytes",
                            candidate.Resource.SizeBytes.Value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("budget");
            ProtocolJson.ToElement(context.BudgetReport).WriteTo(writer);
            writer.WriteEndObject();
        }

        return Message(
            messageId,
            NormalizedRoles.User,
            JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone(),
            createdAt);
    }

    public static NormalizedMessage SkillMessage(
        string messageId,
        SkillDisclosurePlan disclosure,
        DateTimeOffset createdAt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.skills+json");
            writer.WritePropertyName("catalog");
            writer.WriteStartArray();
            foreach (var summary in disclosure.Catalog)
            {
                writer.WriteStartObject();
                writer.WriteString("skillId", summary.SkillId);
                writer.WriteString("version", summary.Version);
                writer.WriteString("digest", summary.Digest);
                writer.WriteString("description", summary.Description);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("activated");
            writer.WriteStartArray();
            foreach (var skill in disclosure.Activated)
            {
                writer.WriteStartObject();
                writer.WriteString("skillId", skill.SkillId);
                writer.WriteString("version", skill.Version);
                writer.WriteString("digest", skill.ContentDigest);
                writer.WritePropertyName("instructions");
                writer.WriteStartArray();
                foreach (var fragment in skill.PromptFragments)
                {
                    writer.WriteStringValue(fragment);
                }

                writer.WriteEndArray();
                writer.WritePropertyName("requiredTools");
                WriteStrings(writer, skill.RequiredToolReferences);
                writer.WritePropertyName("optionalTools");
                WriteStrings(writer, skill.OptionalToolReferences);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("deferred");
            WriteStrings(writer, disclosure.DeferredReferences);
            writer.WriteEndObject();
        }

        return Message(
            messageId,
            NormalizedRoles.System,
            JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone(),
            createdAt);
    }

    public static NormalizedMessage SkillContentMessage(
        string messageId,
        SkillContentResolutionSelection selection,
        DateTimeOffset createdAt)
    {
        if (selection is null)
        {
            throw new ArgumentNullException(nameof(selection));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.skill-content+json");
            writer.WriteString("authority", "non_authoritative");
            writer.WriteString("usage", "context_only");
            writer.WriteBoolean("truncated", selection.Truncated);
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in selection.Items)
            {
                SkillContentResolutionSelection.WriteIdentity(writer, item);
                writer.WriteString("authority", "non_authoritative");
                if (item.Content.HasValue)
                {
                    writer.WritePropertyName("content");
                    item.Content.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Message(
            messageId,
            NormalizedRoles.User,
            JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone(),
            createdAt);
    }

    public static JsonElement ControlPayload(RunControlCommand command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("commandId", command.CommandId);
            writer.WriteString("kind", command.Kind);
            writer.WriteString("createdAt", command.CreatedAt);
            if (command.Observation is not null)
            {
                writer.WriteString(
                    "observationId",
                    command.Observation.ObservationId);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static JsonElement ErrorPayload(
        string code,
        string category,
        string safeMessage)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("category", category);
            writer.WriteString("message", safeMessage);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static JsonElement FinalOutput(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonArrayBuilder.String(text);
        }
    }

    public static ToolDescriptor ToDescriptor(ToolCatalogEntry entry)
    {
        return new ToolDescriptor
        {
            Name = entry.Name,
            Version = entry.Version,
            Description = entry.Description,
            ParametersSchema = entry.ParametersSchema.Clone(),
            ResultSchema = entry.ResultSchema?.Clone(),
            Effect = entry.Effect,
            ConflictScopes = entry.ConflictScopes.ToList(),
            ThreadAffinity = entry.ThreadAffinity,
            TimeoutMs = entry.TimeoutMs,
            RetryPolicy = entry.RetryPolicy,
            IdempotencyPolicy = entry.IdempotencyPolicy,
            Toolset = entry.Toolset,
            Visibility = entry.Visibility,
            Extensions = entry.Extensions.ToDictionary(
                item => item.Key,
                item => item.Value.Clone(),
                StringComparer.Ordinal)
        };
    }

    public static string TranscriptDigest(
        IReadOnlyList<NormalizedMessage> transcript,
        ToolCatalogSnapshot tools,
        SkillCatalogSnapshot skills)
    {
        return TranscriptDigest(transcript, tools.Digest, skills);
    }

    public static string TranscriptDigest(
        IReadOnlyList<NormalizedMessage> transcript,
        string effectiveToolDigest,
        SkillCatalogSnapshot skills)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "prompt");
        digest.Add(
            "toolDigest",
            RuntimeGuard.RequiredUtf8(
                effectiveToolDigest,
                256,
                nameof(effectiveToolDigest)));
        digest.Add("skillDigest", skills.Digest);
        foreach (var message in transcript)
        {
            digest.Add("messageId", message.MessageId);
            digest.Add(
                "message",
                NormalizedMessageJournalCodec.Encode(message));
        }

        return digest.Finish();
    }

    public static string StablePrefixDigest(
        IReadOnlyList<NormalizedMessage> prefix,
        string promptLayoutVersion)
    {
        if (prefix is null)
        {
            throw new ArgumentNullException(nameof(prefix));
        }

        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "stable-prompt-prefix");
        digest.Add(
            "promptLayoutVersion",
            RuntimeGuard.RequiredUtf8(
                promptLayoutVersion,
                64,
                nameof(promptLayoutVersion)));
        digest.Add("messageCount", prefix.Count);
        foreach (var message in prefix)
        {
            if (message is null)
            {
                throw new ArgumentException(
                    "Stable prompt prefixes cannot contain null messages.",
                    nameof(prefix));
            }

            digest.Add("role", message.Role);
            digest.Add("partCount", message.Parts.Count);
            foreach (var part in message.Parts)
            {
                digest.Add("partType", part.Type);
                digest.Add("text", part.Text);
                digest.Add("toolCallId", part.ToolCallId);
                digest.Add("toolName", part.ToolName);
                digest.Add("toolVersion", part.ToolVersion);
                digest.Add("toolEffect", part.ToolEffect);
                digest.Add(
                    "toolDescriptorDigest",
                    part.ToolDescriptorDigest);
                if (part.Json.HasValue)
                {
                    digest.Add("json", part.Json.Value);
                }
                else
                {
                    digest.Add("json", string.Empty);
                }
            }
        }

        return digest.Finish();
    }

    public static string AddCost(string current, string delta)
    {
        if (!decimal.TryParse(
                current,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var currentValue)
            || !decimal.TryParse(
                delta,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var deltaValue)
            || currentValue < 0
            || deltaValue < 0)
        {
            throw new InvalidDataException("Provider usage cost is invalid.");
        }

        decimal total;
        try
        {
            total = currentValue + deltaValue;
        }
        catch (OverflowException)
        {
            total = decimal.MaxValue;
        }

        return total.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);
    }

    private static NormalizedMessage Message(
        string messageId,
        string role,
        JsonElement content,
        DateTimeOffset createdAt)
    {
        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = role,
            CreatedAt = createdAt,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromJson(content)
            }
        };
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void EnsurePromptBytes(
        long utf8Bytes,
        int maxUtf8Bytes,
        string parameterName)
    {
        if (utf8Bytes > maxUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "prompt_bytes_exceeded",
                $"Prompt content exceeds {maxUtf8Bytes} UTF-8 bytes.");
        }
    }
}

internal sealed class PromptMeasurement
{
    public PromptMeasurement(
        int utf8Bytes,
        int estimatedTokens,
        string estimatorId,
        string estimatorVersion)
    {
        Utf8Bytes = utf8Bytes;
        EstimatedTokens = estimatedTokens;
        EstimatorId = RuntimeGuard.RequiredUtf8(
            estimatorId,
            128,
            nameof(estimatorId));
        EstimatorVersion = RuntimeGuard.RequiredUtf8(
            estimatorVersion,
            64,
            nameof(estimatorVersion));
    }

    public int Utf8Bytes { get; }

    public int EstimatedTokens { get; }

    public string EstimatorId { get; }

    public string EstimatorVersion { get; }
}
