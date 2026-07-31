using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GodotArray = global::Godot.Collections.Array;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

public static class GodotProtocolVariantMapper
{
    private const int MaximumOptionsUtf8Bytes = 1_048_576;
    private const int MaximumContextUtf8Bytes = 262_144;
    private const int MaximumActionReceiptUtf8Bytes = 262_144;
    private const int MaximumActiveSkills = 128;
    private const int MaximumTranscriptMessages = 2_048;
    private const int MaximumContextCandidates = 512;
    private const int MaximumMessageParts = 256;
    private const int MaximumLaneIdUtf8Bytes = 256;
    private const int MaximumMessageIdUtf8Bytes = 128;
    private const int MaximumIngressAggregateUtf8Bytes =
        16 * 1_048_576;
    private const int MaximumIngressAggregateNodes = 262_144;

    public static DurableRunRequest ToDurableRunRequest(
        GodotDictionary run,
        GodotArray observations)
    {
        return ToDurableRunRequestCore(
            run,
            observations,
            activeSkills: Array.Empty<SkillReference>(),
            initialTranscript: Array.Empty<NormalizedMessage>(),
            laneId: null,
            ProviderWorkloadClasses.Interactive);
    }

    public static DurableRunRequest ToDurableRunRequest(
        GodotDictionary run,
        GodotArray observations,
        GodotDictionary options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var ingressBudget = new GodotVariantIngressBudget(
            MaximumIngressAggregateUtf8Bytes,
            MaximumIngressAggregateNodes);
        using var document = ParseOptions(options, ingressBudget);
        var root = document.RootElement;
        RejectUnknown(
            root,
            "active_skills",
            "workload_class",
            "lane_id",
            "initial_transcript");

        return ToDurableRunRequestCore(
            run,
            observations,
            ReadActiveSkills(root),
            ReadInitialTranscript(root),
            ReadOptionalLaneId(root),
            ReadStartWorkloadClass(root),
            ingressBudget);
    }

    public static DurableRunContinuation ToDurableRunContinuation(
        GodotDictionary options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        using var document = ParseOptions(options);
        var root = document.RootElement;
        RejectUnknown(
            root,
            "context",
            "active_skills",
            "replace_active_skills",
            "lane_id",
            "workload_class");

        return ReadDurableRunContinuation(root);
    }

    public static GodotDurableResumeOptions ToDurableResumeOptions(
        GodotDictionary options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        using var document = ParseOptions(options);
        var root = document.RootElement;
        RejectUnknown(
            root,
            "context",
            "active_skills",
            "replace_active_skills",
            "lane_id",
            "workload_class",
            "resume_guard");

        return new GodotDurableResumeOptions
        {
            Continuation = ReadDurableRunContinuation(root),
            Guard = ReadOptionalResumeGuard(root)
        };
    }

    public static GodotParticipantResumeOptions ToParticipantResumeOptions(
        GodotDictionary options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        using var document = ParseOptions(options);
        var root = document.RootElement;
        RejectUnknown(
            root,
            "context",
            "active_skills",
            "replace_active_skills",
            "lane_id",
            "workload_class",
            "semantic_expectation");

        DurableRunSemanticExpectation? semanticExpectation = null;
        if (root.TryGetProperty(
                "semantic_expectation",
                out var expectation))
        {
            EnsureObject(
                expectation,
                "options.semantic_expectation");
            RejectUnknown(
                expectation,
                "extension_name",
                "expected_sha256");
            semanticExpectation = new DurableRunSemanticExpectation(
                ReadRequiredString(
                    expectation,
                    "extension_name",
                    128,
                    "options.semantic_expectation.extension_name"),
                ReadRequiredString(
                    expectation,
                    "expected_sha256",
                    64,
                    "options.semantic_expectation.expected_sha256"));
        }

        return new GodotParticipantResumeOptions
        {
            Continuation = ReadDurableRunContinuation(root),
            SemanticExpectation = semanticExpectation
        };
    }

    private static DurableRunContinuation ReadDurableRunContinuation(
        JsonElement root)
    {
        return new DurableRunContinuation
        {
            Context = ReadContext(root),
            ActiveSkills = ReadActiveSkills(root),
            ReplaceActiveSkills = ReadOptionalBoolean(
                root,
                "replace_active_skills"),
            LaneId = ReadOptionalLaneId(root),
            WorkloadClass = ReadResumeWorkloadClass(root)
        };
    }

    private static DurableRunResumeGuard? ReadOptionalResumeGuard(
        JsonElement options)
    {
        if (!options.TryGetProperty("resume_guard", out var value))
        {
            return null;
        }

        EnsureObject(value, "options.resume_guard");
        RejectUnknown(
            value,
            "expected_batch_id",
            "expected_agent_id",
            "expected_decision_key",
            "required_int32_extension_name",
            "minimum_int32_extension_value",
            "maximum_int32_extension_value",
            "expected_int32_extension_value",
            "semantic_extension_name",
            "expected_semantic_extension_sha256");

        var expectedBatchId = ReadOptionalString(
            value,
            "expected_batch_id",
            128,
            "options.resume_guard.expected_batch_id");
        var expectedAgentId = ReadOptionalString(
            value,
            "expected_agent_id",
            128,
            "options.resume_guard.expected_agent_id");
        var expectedDecisionKey = ReadOptionalString(
            value,
            "expected_decision_key",
            256,
            "options.resume_guard.expected_decision_key");
        var int32ExtensionName = ReadOptionalString(
            value,
            "required_int32_extension_name",
            128,
            "options.resume_guard.required_int32_extension_name");
        var minimum = ReadOptionalInt32(
            value,
            "minimum_int32_extension_value",
            int.MinValue,
            int.MaxValue,
            int.MinValue);
        var maximum = ReadOptionalInt32(
            value,
            "maximum_int32_extension_value",
            int.MinValue,
            int.MaxValue,
            int.MaxValue);
        var expectedInt32 = ReadOptionalNullableInt32(
            value,
            "expected_int32_extension_value",
            int.MinValue,
            int.MaxValue);
        var semanticExtensionName = ReadOptionalString(
            value,
            "semantic_extension_name",
            128,
            "options.resume_guard.semantic_extension_name");
        var semanticDigest = ReadOptionalString(
            value,
            "expected_semantic_extension_sha256",
            64,
            "options.resume_guard.expected_semantic_extension_sha256");

        if (expectedBatchId is null
            && expectedAgentId is null
            && expectedDecisionKey is null
            && int32ExtensionName is null
            && semanticExtensionName is null)
        {
            throw new JsonException(
                "options.resume_guard must contain at least one expectation.");
        }

        if (int32ExtensionName is null
            && (minimum != int.MinValue
                || maximum != int.MaxValue
                || expectedInt32.HasValue))
        {
            throw new JsonException(
                "options.resume_guard requires required_int32_extension_name when Int32 constraints are supplied.");
        }

        if (minimum > maximum
            || expectedInt32 is int expected
            && (expected < minimum || expected > maximum))
        {
            throw new JsonException(
                "options.resume_guard contains inconsistent Int32 bounds.");
        }

        if ((semanticExtensionName is null) != (semanticDigest is null)
            || semanticDigest is not null
            && !CanonicalJsonDigest.IsSha256(semanticDigest))
        {
            throw new JsonException(
                "options.resume_guard semantic expectation requires a name and a 64-character lowercase SHA-256 digest.");
        }

        return new DurableRunResumeGuard
        {
            ExpectedBatchId = expectedBatchId,
            ExpectedAgentId = expectedAgentId,
            ExpectedDecisionKey = expectedDecisionKey,
            RequiredInt32ExtensionName = int32ExtensionName,
            MinimumInt32ExtensionValue = minimum,
            MaximumInt32ExtensionValue = maximum,
            ExpectedInt32ExtensionValue = expectedInt32,
            SemanticExtensionName = semanticExtensionName,
            ExpectedSemanticExtensionSha256 = semanticDigest
        };
    }

    private static DurableRunRequest ToDurableRunRequestCore(
        GodotDictionary run,
        GodotArray observations,
        IReadOnlyList<SkillReference> activeSkills,
        IReadOnlyList<NormalizedMessage> initialTranscript,
        string? laneId,
        string workloadClass,
        GodotVariantIngressBudget? sharedIngressBudget = null)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (observations is null)
        {
            throw new ArgumentNullException(nameof(observations));
        }

        if (observations.Count > MaximumContextCandidates)
        {
            throw new JsonException(
                $"observations cannot exceed {MaximumContextCandidates} items.");
        }

        var ingressBudget = sharedIngressBudget
            ?? new GodotVariantIngressBudget(
                MaximumIngressAggregateUtf8Bytes,
                MaximumIngressAggregateNodes);
        var mappedRun = ReadAgentRun(
            GodotVariantInputGuard.StringifyAndNormalizeDictionary(
                run,
                "run",
                maximumUtf8Bytes: 1_048_576,
                ingressBudget: ingressBudget));
        var context = new List<ContextCandidate>(observations.Count);
        for (var index = 0; index < observations.Count; index++)
        {
            var value = observations[index];
            string json;
            try
            {
                json = StringifyObject(
                    value,
                    $"observations[{index}]",
                    ingressBudget);
            }
            finally
            {
                value.Dispose();
            }

            var observation = ReadObservation(json);
            context.Add(
                ContextCandidate.FromObservation(
                    observation,
                    mappedRun,
                    required: true,
                    canDefer: false));
        }

        return new DurableRunRequest
        {
            Run = mappedRun,
            Context = context,
            ActiveSkills = activeSkills,
            InitialTranscript = initialTranscript,
            LaneId = laneId,
            WorkloadClass = workloadClass
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

        if (observations.Count > MaximumContextCandidates)
        {
            throw new JsonException(
                $"observations cannot exceed "
                + $"{MaximumContextCandidates} items.");
        }

        if (tools.Count > MaximumContextCandidates)
        {
            throw new JsonException(
                $"tools cannot exceed {MaximumContextCandidates} items.");
        }

        var ingressBudget = new GodotVariantIngressBudget(
            MaximumIngressAggregateUtf8Bytes,
            MaximumIngressAggregateNodes);
        var mappedRun = ReadAgentRun(
            GodotVariantInputGuard.StringifyAndNormalizeDictionary(
                run,
                "run",
                maximumUtf8Bytes: 1_048_576,
                ingressBudget: ingressBudget));
        var mappedObservations = new List<ObservationEnvelope>(observations.Count);
        for (var index = 0; index < observations.Count; index++)
        {
            var value = observations[index];
            try
            {
                mappedObservations.Add(
                    ReadObservation(
                        StringifyObject(
                            value,
                            $"observations[{index}]",
                            ingressBudget)));
            }
            finally
            {
                value.Dispose();
            }
        }

        var mappedTools = new List<ToolDescriptor>(tools.Count);
        for (var index = 0; index < tools.Count; index++)
        {
            var value = tools[index];
            try
            {
                mappedTools.Add(
                    ReadToolDescriptor(
                        StringifyObject(
                            value,
                            $"tools[{index}]",
                            ingressBudget)));
            }
            finally
            {
                value.Dispose();
            }
        }

        return new HeadlessRunRequest
        {
            Run = mappedRun,
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

        return ReadObservation(
            GodotVariantInputGuard.StringifyAndNormalizeDictionary(
                observation,
            "observation",
            maximumUtf8Bytes: 1_048_576));
    }

    public static ActionReceipt ToActionReceipt(
        GodotDictionary receipt)
    {
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        var value = ProtocolJson.DeserializeActionReceipt(
            GodotVariantInputGuard.StringifyAndNormalizeDictionary(
                receipt,
                "action_receipt",
                MaximumActionReceiptUtf8Bytes));
        ProtocolValidator.EnsureValid(value);
        return value;
    }

    private static AgentRun ReadAgentRun(string json)
    {
        var value = ProtocolJson.DeserializeAgentRun(json);
        ProtocolValidator.EnsureValid(value);
        return value;
    }

    private static ObservationEnvelope ReadObservation(string json)
    {
        var value = ProtocolJson.DeserializeObservationEnvelope(json);
        ProtocolValidator.EnsureValid(value);
        return value;
    }

    private static ToolDescriptor ReadToolDescriptor(string json)
    {
        var value = ProtocolJson.DeserializeToolDescriptor(json);
        ProtocolValidator.EnsureValid(value);
        return value;
    }

    public static GodotDictionary ToDictionary(AgentRun value) =>
        ToDictionary(ProtocolJson.ToElement(value));

    public static GodotDictionary ToDictionary(RuntimeEvent value) =>
        ToDictionary(ProtocolJson.ToElement(value));

    public static GodotDictionary ToDictionary(ObservationEnvelope value) =>
        ToDictionary(ProtocolJson.ToElement(value));

    public static GodotDictionary ToDictionary(ActionReceipt value) =>
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

    private static JsonDocument ParseOptions(
        GodotDictionary options,
        GodotVariantIngressBudget? ingressBudget = null)
    {
        var json =
            GodotVariantInputGuard.StringifyAndNormalizeDictionary(
                options,
                "options",
                MaximumOptionsUtf8Bytes,
                ingressBudget);

        var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new JsonException("options must be a Dictionary.");
        }

        return document;
    }

    private static IReadOnlyList<SkillReference> ReadActiveSkills(
        JsonElement options)
    {
        if (!options.TryGetProperty("active_skills", out var value))
        {
            return Array.Empty<SkillReference>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("options.active_skills must be an Array.");
        }

        if (value.GetArrayLength() > MaximumActiveSkills)
        {
            throw new JsonException(
                $"options.active_skills cannot exceed {MaximumActiveSkills} items.");
        }

        var result = new List<SkillReference>(value.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            EnsureObject(item, $"options.active_skills[{index}]");
            RejectUnknown(item, "skill_id", "version");
            var skill = new SkillReference(
                ReadRequiredString(
                    item,
                    "skill_id",
                    128,
                    $"options.active_skills[{index}].skill_id"),
                ReadRequiredString(
                    item,
                    "version",
                    32,
                    $"options.active_skills[{index}].version"));
            if (!seen.Add(skill.Value))
            {
                throw new JsonException(
                    $"options.active_skills[{index}] is duplicated.");
            }

            result.Add(skill);
            index++;
        }

        return result;
    }

    private static IReadOnlyList<NormalizedMessage> ReadInitialTranscript(
        JsonElement options)
    {
        if (!options.TryGetProperty("initial_transcript", out var value))
        {
            return Array.Empty<NormalizedMessage>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(
                "options.initial_transcript must be an Array.");
        }

        if (value.GetArrayLength() > MaximumTranscriptMessages)
        {
            throw new JsonException(
                "options.initial_transcript cannot exceed "
                + $"{MaximumTranscriptMessages} items.");
        }

        var result = new List<NormalizedMessage>(value.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var message = NormalizedMessageJournalCodec.Decode(
                ProtocolJson.DeserializeJsonElement(item.GetRawText()));
            ValidateMessage(message, index);
            if (!ids.Add(message.MessageId))
            {
                throw new JsonException(
                    $"options.initial_transcript[{index}].messageId is duplicated.");
            }

            result.Add(message);
            index++;
        }

        return result;
    }

    private static void ValidateMessage(
        NormalizedMessage message,
        int index)
    {
        ValidateRequiredUtf8(
            message.MessageId,
            MaximumMessageIdUtf8Bytes,
            $"options.initial_transcript[{index}].messageId");
        if (!IsIdentifier(message.MessageId))
        {
            throw new JsonException(
                $"options.initial_transcript[{index}].messageId is invalid.");
        }

        if (message.Role is not (
                NormalizedRoles.System
                or NormalizedRoles.User
                or NormalizedRoles.Assistant
                or NormalizedRoles.Tool))
        {
            throw new JsonException(
                $"options.initial_transcript[{index}].role is unsupported.");
        }

        if (message.Parts.Count > MaximumMessageParts)
        {
            throw new JsonException(
                $"options.initial_transcript[{index}].parts cannot exceed "
                + $"{MaximumMessageParts} items.");
        }
    }

    private static IReadOnlyList<ContextCandidate> ReadContext(
        JsonElement options)
    {
        if (!options.TryGetProperty("context", out var value))
        {
            return Array.Empty<ContextCandidate>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("options.context must be an Array.");
        }

        if (Encoding.UTF8.GetByteCount(value.GetRawText())
            > MaximumContextUtf8Bytes)
        {
            throw new JsonException(
                $"options.context exceeds {MaximumContextUtf8Bytes} UTF-8 bytes.");
        }

        if (value.GetArrayLength() > MaximumContextCandidates)
        {
            throw new JsonException(
                $"options.context cannot exceed {MaximumContextCandidates} items.");
        }

        var result = new List<ContextCandidate>(value.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var path = $"options.context[{index}]";
            EnsureObject(item, path);
            RejectUnknown(
                item,
                "id",
                "category",
                "content",
                "resource",
                "priority",
                "required",
                "can_defer",
                "estimated_tokens",
                "expires_at",
                "provenance");
            var id = ReadRequiredString(item, "id", 128, $"{path}.id");
            var category = ReadRequiredString(
                item,
                "category",
                64,
                $"{path}.category");
            if (!ids.Add(id))
            {
                throw new JsonException($"{path}.id is duplicated.");
            }

            var priority = ReadOptionalInt32(
                item,
                "priority",
                minimum: -1_000,
                maximum: 1_000,
                defaultValue: 0);
            var required = ReadOptionalBoolean(item, "required");
            var canDefer = ReadOptionalBoolean(
                item,
                "can_defer",
                defaultValue: true);
            var estimatedTokens = ReadOptionalNullableInt32(
                item,
                "estimated_tokens",
                minimum: 0,
                maximum: 1_000_000);
            var expiresAt = ReadOptionalDateTimeOffset(
                item,
                "expires_at",
                $"{path}.expires_at");
            var provenance = ReadOptionalString(
                item,
                "provenance",
                256,
                $"{path}.provenance");

            var hasContent = item.TryGetProperty("content", out var content);
            var hasResource = item.TryGetProperty("resource", out var resource);
            if (hasContent == hasResource)
            {
                throw new JsonException(
                    $"{path} must contain exactly one of content or resource.");
            }

            result.Add(
                hasContent
                    ? new ContextCandidate(
                        id,
                        category,
                        ProtocolJson.DeserializeJsonElement(
                            content.GetRawText()),
                        priority,
                        required,
                        canDefer,
                        estimatedTokens,
                        expiresAt,
                        provenance)
                    : new ContextCandidate(
                        id,
                        category,
                        ReadContextResource(resource, $"{path}.resource"),
                        priority,
                        required,
                        canDefer,
                        estimatedTokens,
                        expiresAt,
                        provenance));
            index++;
        }

        return result;
    }

    private static ContextResourceReference ReadContextResource(
        JsonElement value,
        string path)
    {
        EnsureObject(value, path);
        RejectUnknown(value, "uri", "media_type", "digest", "size_bytes");
        return new ContextResourceReference(
            ReadRequiredString(value, "uri", 2_048, $"{path}.uri"),
            ReadRequiredString(
                value,
                "media_type",
                128,
                $"{path}.media_type"),
            ReadOptionalString(value, "digest", 256, $"{path}.digest"),
            ReadOptionalNullableInt64(
                value,
                "size_bytes",
                minimum: 0,
                maximum: long.MaxValue));
    }

    private static string ReadStartWorkloadClass(JsonElement options)
    {
        return ReadWorkloadClass(options)
               ?? ProviderWorkloadClasses.Interactive;
    }

    private static string? ReadResumeWorkloadClass(JsonElement options) =>
        ReadWorkloadClass(options);

    private static string? ReadWorkloadClass(JsonElement options)
    {
        if (!options.TryGetProperty("workload_class", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                "options.workload_class must be a String.");
        }

        var result = value.GetString();
        if (result is not (
                ProviderWorkloadClasses.Interactive
                or ProviderWorkloadClasses.Background))
        {
            throw new JsonException(
                "options.workload_class must be 'interactive' or 'background'.");
        }

        return result;
    }

    private static string? ReadOptionalLaneId(JsonElement options) =>
        ReadOptionalString(
            options,
            "lane_id",
            MaximumLaneIdUtf8Bytes,
            "options.lane_id");

    private static string ReadRequiredString(
        JsonElement value,
        string propertyName,
        int maximumUtf8Bytes,
        string path)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{path} must be a String.");
        }

        var result = property.GetString();
        ValidateRequiredUtf8(result, maximumUtf8Bytes, path);
        return result!;
    }

    private static string? ReadOptionalString(
        JsonElement value,
        string propertyName,
        int maximumUtf8Bytes,
        string path)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{path} must be a String.");
        }

        var result = property.GetString();
        ValidateRequiredUtf8(result, maximumUtf8Bytes, path);
        return result;
    }

    private static void ValidateRequiredUtf8(
        string? value,
        int maximumUtf8Bytes,
        string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"{path} must not be empty.");
        }

        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new JsonException(
                $"{path} exceeds {maximumUtf8Bytes} UTF-8 bytes.");
        }
    }

    private static bool ReadOptionalBoolean(
        JsonElement value,
        string propertyName,
        bool defaultValue = false)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return defaultValue;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException(
                $"options.{propertyName} must be a Boolean.")
        };
    }

    private static int ReadOptionalInt32(
        JsonElement value,
        string propertyName,
        int minimum,
        int maximum,
        int defaultValue)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return defaultValue;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var result)
            || result < minimum
            || result > maximum)
        {
            throw new JsonException(
                $"options.{propertyName} must be an integer from "
                + $"{minimum} through {maximum}.");
        }

        return result;
    }

    private static int? ReadOptionalNullableInt32(
        JsonElement value,
        string propertyName,
        int minimum,
        int maximum)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var result)
            || result < minimum
            || result > maximum)
        {
            throw new JsonException(
                $"options.{propertyName} must be an integer from "
                + $"{minimum} through {maximum}.");
        }

        return result;
    }

    private static long? ReadOptionalNullableInt64(
        JsonElement value,
        string propertyName,
        long minimum,
        long maximum)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var result)
            || result < minimum
            || result > maximum)
        {
            throw new JsonException(
                $"options.{propertyName} must be an integer from "
                + $"{minimum} through {maximum}.");
        }

        return result;
    }

    private static DateTimeOffset? ReadOptionalDateTimeOffset(
        JsonElement value,
        string propertyName,
        string path)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String
            || !property.TryGetDateTimeOffset(out var result))
        {
            throw new JsonException($"{path} must be an RFC 3339 timestamp.");
        }

        return result;
    }

    private static void EnsureObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{path} must be a Dictionary.");
        }
    }

    private static void RejectUnknown(
        JsonElement value,
        params string[] allowed)
    {
        EnsureObject(value, "options");
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException(
                    $"Unknown options field '{property.Name}'.");
            }
        }
    }

    private static bool IsIdentifier(string value)
    {
        foreach (var character in value)
        {
            var allowed = character is >= 'A' and <= 'Z'
                          || character is >= 'a' and <= 'z'
                          || character is >= '0' and <= '9'
                          || character is '.' or '_' or ':' or '-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static string StringifyObject(
        global::Godot.Variant value,
        string path,
        GodotVariantIngressBudget? ingressBudget = null)
    {
        if (value.VariantType != global::Godot.Variant.Type.Dictionary)
        {
            throw new JsonException($"{path} must be a Dictionary.");
        }

        using var dictionary = value.AsGodotDictionary();
        return GodotVariantInputGuard.StringifyAndNormalizeDictionary(
            dictionary,
            path,
            maximumUtf8Bytes: 1_048_576,
            ingressBudget: ingressBudget);
    }

    internal static string NormalizeJsonNumbers(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder =
                           JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
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

internal sealed class GodotVariantIngressBudget
{
    private readonly int _maximumUtf8Bytes;
    private readonly int _maximumNodes;
    private long _estimatedUtf8Bytes;
    private long _rawUtf8Bytes;
    private long _normalizedUtf8Bytes;
    private long _nodes;

    internal GodotVariantIngressBudget(
        int maximumUtf8Bytes,
        int maximumNodes)
    {
        if (maximumUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumUtf8Bytes));
        }

        if (maximumNodes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        }

        _maximumUtf8Bytes = maximumUtf8Bytes;
        _maximumNodes = maximumNodes;
    }

    internal void AdmitGraph(
        int nodes,
        long estimatedUtf8Bytes,
        string path)
    {
        _nodes += nodes;
        _estimatedUtf8Bytes += estimatedUtf8Bytes;
        if (_nodes > _maximumNodes)
        {
            throw new JsonException(
                $"{path} exceeds the aggregate Variant node budget of "
                + $"{_maximumNodes}.");
        }

        if (_estimatedUtf8Bytes > _maximumUtf8Bytes)
        {
            throw new JsonException(
                $"{path} exceeds the aggregate Variant UTF-8 budget of "
                + $"{_maximumUtf8Bytes} bytes.");
        }
    }

    internal void AdmitRaw(int utf8Bytes, string path)
    {
        _rawUtf8Bytes += utf8Bytes;
        if (_rawUtf8Bytes > _maximumUtf8Bytes)
        {
            throw new JsonException(
                $"{path} exceeds the aggregate serialized UTF-8 budget of "
                + $"{_maximumUtf8Bytes} bytes.");
        }
    }

    internal void AdmitNormalized(int utf8Bytes, string path)
    {
        _normalizedUtf8Bytes += utf8Bytes;
        if (_normalizedUtf8Bytes > _maximumUtf8Bytes)
        {
            throw new JsonException(
                $"{path} exceeds the aggregate normalized UTF-8 budget of "
                + $"{_maximumUtf8Bytes} bytes.");
        }
    }
}

internal static class GodotVariantInputGuard
{
    private const int MaximumDepth = 64;
    private const int MaximumContainerItems = 2_048;
    private const int MaximumStringUtf8Bytes = 65_536;
    private const int MaximumObjectNodes = 131_072;
    private const int MaximumLargeObjectNodes = 262_144;

    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    internal static string StringifyAndNormalizeDictionary(
        GodotDictionary value,
        string path,
        int maximumUtf8Bytes,
        GodotVariantIngressBudget? ingressBudget = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maximumUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumUtf8Bytes));
        }

        var maximumNodes = maximumUtf8Bytes > 1_048_576
            ? MaximumLargeObjectNodes
            : MaximumObjectNodes;
        var graph = new VariantGraphCounter(
            maximumUtf8Bytes,
            maximumNodes);
        graph.VisitDictionary(value, depth: 1, path);

        var budget = ingressBudget
            ?? new GodotVariantIngressBudget(
                maximumUtf8Bytes,
                maximumNodes);
        budget.AdmitGraph(graph.Nodes, graph.EstimatedUtf8Bytes, path);

        var raw = global::Godot.Json.Stringify(value);
        var rawUtf8Bytes = GetUtf8ByteCount(raw, path);
        if (rawUtf8Bytes > maximumUtf8Bytes)
        {
            throw new JsonException(
                $"{path} exceeds {maximumUtf8Bytes} UTF-8 bytes.");
        }

        budget.AdmitRaw(rawUtf8Bytes, path);

        var normalized =
            GodotProtocolVariantMapper.NormalizeJsonNumbers(raw);
        var normalizedUtf8Bytes =
            GetUtf8ByteCount(normalized, path);
        if (normalizedUtf8Bytes > maximumUtf8Bytes)
        {
            throw new JsonException(
                $"{path} exceeds {maximumUtf8Bytes} normalized UTF-8 bytes.");
        }

        budget.AdmitNormalized(normalizedUtf8Bytes, path);
        return normalized;
    }

    private static int GetUtf8ByteCount(string value, string path)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new JsonException(
                $"{path} contains invalid UTF-16.");
        }
    }

    private sealed class VariantGraphCounter
    {
        private readonly int _maximumUtf8Bytes;
        private readonly int _maximumNodes;

        internal VariantGraphCounter(
            int maximumUtf8Bytes,
            int maximumNodes)
        {
            _maximumUtf8Bytes = maximumUtf8Bytes;
            _maximumNodes = maximumNodes;
        }

        internal int Nodes { get; private set; }

        internal long EstimatedUtf8Bytes { get; private set; }

        internal void VisitDictionary(
            GodotDictionary value,
            int depth,
            string path)
        {
            EnsureDepth(depth, path);
            var count = value.Count;
            EnsureContainerCount(count, path);
            AddNode(2L + Math.Max(0, count - 1), path);
            foreach (var pair in value)
            {
                var key = pair.Key;
                var child = pair.Value;
                try
                {
                    if (key.VariantType
                        is not global::Godot.Variant.Type.String
                        and not global::Godot.Variant.Type.StringName)
                    {
                        throw new JsonException(
                            $"{path} contains a non-string Dictionary key.");
                    }

                    AddString(key.AsString(), path, syntaxBytes: 3);
                    VisitValue(child, depth + 1, path);
                }
                finally
                {
                    key.Dispose();
                    child.Dispose();
                }
            }
        }

        private void VisitArray(
            GodotArray value,
            int depth,
            string path)
        {
            EnsureDepth(depth, path);
            var count = value.Count;
            EnsureContainerCount(count, path);
            AddNode(2L + Math.Max(0, count - 1), path);
            for (var index = 0; index < count; index++)
            {
                var child = value[index];
                try
                {
                    VisitValue(
                        child,
                        depth + 1,
                        $"{path}[{index}]");
                }
                finally
                {
                    child.Dispose();
                }
            }
        }

        private void VisitValue(
            global::Godot.Variant value,
            int depth,
            string path)
        {
            EnsureDepth(depth, path);
            switch (value.VariantType)
            {
                case global::Godot.Variant.Type.Nil:
                    AddNode(4, path);
                    return;
                case global::Godot.Variant.Type.Bool:
                    AddNode(5, path);
                    return;
                case global::Godot.Variant.Type.Int:
                    AddNode(24, path);
                    return;
                case global::Godot.Variant.Type.Float:
                    if (!double.IsFinite(value.AsDouble()))
                    {
                        throw new JsonException(
                            $"{path} contains a non-finite number.");
                    }

                    AddNode(32, path);
                    return;
                case global::Godot.Variant.Type.String:
                case global::Godot.Variant.Type.StringName:
                    AddString(value.AsString(), path, syntaxBytes: 2);
                    return;
                case global::Godot.Variant.Type.Dictionary:
                    using (var dictionary = value.AsGodotDictionary())
                    {
                        VisitDictionary(dictionary, depth, path);
                    }

                    return;
                case global::Godot.Variant.Type.Array:
                    using (var array = value.AsGodotArray())
                    {
                        VisitArray(array, depth, path);
                    }

                    return;
                default:
                    throw new JsonException(
                        $"{path} contains unsupported Variant type "
                        + $"'{value.VariantType}'.");
            }
        }

        private void AddString(
            string value,
            string path,
            int syntaxBytes)
        {
            var utf8Bytes = GetUtf8ByteCount(value, path);
            if (utf8Bytes > MaximumStringUtf8Bytes)
            {
                throw new JsonException(
                    $"{path} contains a string over "
                    + $"{MaximumStringUtf8Bytes} UTF-8 bytes.");
            }

            long escapedUtf8Bytes = 0;
            foreach (var character in value)
            {
                if (character is '"' or '\\')
                {
                    escapedUtf8Bytes += 2;
                }
                else if (character <= '\u001f')
                {
                    escapedUtf8Bytes += 6;
                }
                else if (character <= '\u007f')
                {
                    escapedUtf8Bytes++;
                }
                else
                {
                    // Json.Stringify currently emits Unicode directly, but
                    // count the \uXXXX form as a safe upper bound so a future
                    // engine serializer cannot expand the graph past the
                    // pre-serialization budget.
                    escapedUtf8Bytes += 6;
                }
            }

            AddNode(escapedUtf8Bytes + syntaxBytes, path);
        }

        private void AddNode(long estimatedUtf8Bytes, string path)
        {
            Nodes++;
            EstimatedUtf8Bytes += estimatedUtf8Bytes;
            if (Nodes > _maximumNodes)
            {
                throw new JsonException(
                    $"{path} exceeds {_maximumNodes} Variant nodes.");
            }

            if (EstimatedUtf8Bytes > _maximumUtf8Bytes)
            {
                throw new JsonException(
                    $"{path} exceeds the pre-serialization UTF-8 budget of "
                    + $"{_maximumUtf8Bytes} bytes.");
            }
        }

        private static void EnsureDepth(int depth, string path)
        {
            if (depth > MaximumDepth)
            {
                throw new JsonException(
                    $"{path} is circular or exceeds Variant depth "
                    + $"{MaximumDepth}.");
            }
        }

        private static void EnsureContainerCount(
            int count,
            string path)
        {
            if (count > MaximumContainerItems)
            {
                throw new JsonException(
                    $"{path} contains a Variant container over "
                    + $"{MaximumContainerItems} items.");
            }
        }
    }
}
