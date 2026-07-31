using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using UnityEngine.Scripting;

namespace GameAgent.Unity
{
    [Serializable]
    [Preserve]
    public sealed class UnityAudienceIncarnationData
    {
        public string audienceId = string.Empty;
        public string entityId = string.Empty;
        public long incarnation;
    }

    [Serializable]
    [Preserve]
    public sealed class UnityObservationData
    {
        public string protocolVersion = ProtocolConstants.ProtocolVersion;
        public string schemaVersion = ProtocolConstants.SchemaVersion;
        public string extensionsJson = "{}";
        public string observationId = string.Empty;
        public string worldId = string.Empty;
        public string sessionId = string.Empty;
        public string source = string.Empty;
        public string kind = "custom";
        public string[] subjectIds = new string[0];
        public string contentType = "application/json";
        public string schemaRef = string.Empty;
        public string contentSchemaVersion = string.Empty;
        public string payloadJson = "{}";
        public string resourceUri = string.Empty;
        public string resourceMediaType = "application/json";
        public string resourceDigest = string.Empty;
        public bool hasResourceSizeBytes;
        public long resourceSizeBytes = -1;
        public bool hasObservedAtUnixMilliseconds;
        public long observedAtUnixMilliseconds;
        public bool hasTtlMilliseconds;
        public long ttlMilliseconds;
        public bool hasSequence;
        public long sequence = -1;
        public string stateVersion = string.Empty;
        public string trust = "untrusted";
        public string visibilityScope = "world";
        public string[] audienceIds = new string[0];
        public UnityAudienceIncarnationData[] audienceIncarnations =
            new UnityAudienceIncarnationData[0];
        public int priority;
        public string cacheKey = string.Empty;
    }

    [Serializable]
    [Preserve]
    public sealed class UnityActionRequestData
    {
        public string protocolVersion = ProtocolConstants.ProtocolVersion;
        public string schemaVersion = ProtocolConstants.SchemaVersion;
        public string extensionsJson = "{}";
        public string operationId = string.Empty;
        public string runId = string.Empty;
        public string turnId = string.Empty;
        public string toolCallId = string.Empty;
        public string agentId = string.Empty;
        public string worldId = string.Empty;
        public string actionName = string.Empty;
        public string actionVersion = string.Empty;
        public string argumentsJson = "{}";
        public string decisionKey = string.Empty;
        public string batchId = string.Empty;
        public string basedOnStateVersion = string.Empty;
        public string[] expectedEffects = new string[0];
        public string reasonCode = string.Empty;
        public bool hasRequestedAtUnixMilliseconds;
        public long requestedAtUnixMilliseconds;
        public bool hasDeadlineUnixMilliseconds;
        public long deadlineUnixMilliseconds;
    }

    [Serializable]
    [Preserve]
    public sealed class UnityActionReceiptData
    {
        public string protocolVersion = ProtocolConstants.ProtocolVersion;
        public string schemaVersion = ProtocolConstants.SchemaVersion;
        public string extensionsJson = "{}";
        public string operationId = string.Empty;
        public long revision;
        public string status = ReceiptStatuses.Unknown;
        public string resultJson = string.Empty;
        public string stateDiffJson = string.Empty;
        public UnityObservationData[] authoritativeObservations =
            new UnityObservationData[0];
        public string errorCode = string.Empty;
        public bool retryable;
        public bool hasCommittedAtUnixMilliseconds;
        public long committedAtUnixMilliseconds;
        public bool hasReceivedAtUnixMilliseconds;
        public long receivedAtUnixMilliseconds;
    }

    [Preserve]
    public static class UnityProtocolBridge
    {
        private const int MaximumBridgeDocumentUtf8Bytes =
            32 * 1_048_576;
        private const int MaximumBridgeDocumentJsonDepth = 64;
        private const int MaximumBridgeDocumentJsonNodes = 1_048_576;
        private const int MaximumDtoAggregateJsonUtf8Bytes =
            32 * 1_048_576;
        private const int MaximumDtoAggregateJsonNodes = 1_048_576;

        private enum BridgeDocumentShape
        {
            Generic,
            Observation,
            ActionRequest,
            ActionReceipt,
            RuntimeEvent,
            Visibility,
            Extensions,
            AuthoritativeObservations,
            ExpectedEffects,
            SubjectIds,
            AudienceIds
        }

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);

        public static ObservationEnvelope ToProtocol(
            UnityObservationData value)
        {
            return ToProtocol(
                value,
                new JsonIngressBudget(
                    MaximumDtoAggregateJsonUtf8Bytes,
                    MaximumDtoAggregateJsonNodes));
        }

        private static ObservationEnvelope ToProtocol(
            UnityObservationData value,
            JsonIngressBudget ingressBudget)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var observedAt = RequiredUnixMilliseconds(
                value.observedAtUnixMilliseconds,
                value.hasObservedAtUnixMilliseconds,
                nameof(value.observedAtUnixMilliseconds));
            var hasSequence = IsPresentNonNegative(
                value.sequence,
                value.hasSequence,
                -1,
                nameof(value.sequence));
            var hasResourceSizeBytes = IsPresentNonNegative(
                value.resourceSizeBytes,
                value.hasResourceSizeBytes,
                -1,
                nameof(value.resourceSizeBytes));
            var observation = new ObservationEnvelope
            {
                ProtocolVersion = Required(
                    value.protocolVersion,
                    nameof(value.protocolVersion)),
                SchemaVersion = Required(
                    value.schemaVersion,
                    nameof(value.schemaVersion)),
                Extensions = ParseExtensions(
                    value.extensionsJson,
                    ingressBudget,
                    nameof(value.extensionsJson)),
                ObservationId = Required(
                    value.observationId,
                    nameof(value.observationId)),
                WorldId = Required(value.worldId, nameof(value.worldId)),
                SessionId = EmptyToNull(value.sessionId),
                Source = Required(value.source, nameof(value.source)),
                Kind = Required(value.kind, nameof(value.kind)),
                SubjectIds = Copy(
                    value.subjectIds,
                    ProtocolLimits.MaxObservationSubjectIds,
                    nameof(value.subjectIds)),
                ContentType = Required(
                    value.contentType,
                    nameof(value.contentType)),
                SchemaRef = EmptyToNull(value.schemaRef),
                ContentSchemaVersion =
                    EmptyToNull(value.contentSchemaVersion),
                ObservedAt = observedAt,
                TtlMs = value.hasTtlMilliseconds
                        || value.ttlMilliseconds != 0
                    ? value.ttlMilliseconds
                    : (long?)null,
                Sequence = hasSequence
                    ? value.sequence
                    : (long?)null,
                StateVersion = EmptyToNull(value.stateVersion),
                Trust = Required(value.trust, nameof(value.trust)),
                Visibility = new VisibilityRule
                {
                    Scope = Required(
                        value.visibilityScope,
                        nameof(value.visibilityScope)),
                    AudienceIds = Copy(
                        value.audienceIds,
                        ProtocolLimits.MaxObservationAudienceIds,
                        nameof(value.audienceIds))
                },
                Priority = value.priority,
                CacheKey = EmptyToNull(value.cacheKey)
            };

            var payload = ParseOptionalJson(
                value.payloadJson,
                defaultJson: "{}",
                ingressBudget,
                nameof(value.payloadJson));
            if (!string.IsNullOrWhiteSpace(value.resourceUri))
            {
                observation.ResourceRef = new ResourceReference
                {
                    Uri = value.resourceUri,
                    MediaType = Required(
                        value.resourceMediaType,
                        nameof(value.resourceMediaType)),
                    Digest = EmptyToNull(value.resourceDigest),
                    SizeBytes = hasResourceSizeBytes
                        ? value.resourceSizeBytes
                        : (long?)null
                };
            }
            else
            {
                observation.Payload = payload;
            }

            var audienceIncarnations = value.audienceIncarnations
                ?? new UnityAudienceIncarnationData[0];
            if (audienceIncarnations.Length
                > ObservationAudienceIncarnations.MaxBindings)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value.audienceIncarnations),
                    audienceIncarnations.Length,
                    "audienceIncarnations cannot exceed "
                    + ObservationAudienceIncarnations.MaxBindings
                    + " items.");
            }

            if (audienceIncarnations.Length != 0)
            {
                ObservationAudienceIncarnations.Attach(
                    observation,
                    audienceIncarnations.Select(
                        item =>
                        {
                            if (item == null)
                            {
                                throw new ArgumentException(
                                    "audienceIncarnations cannot contain null entries.",
                                    nameof(value.audienceIncarnations));
                            }

                            return new
                                ObservationAudienceIncarnationBinding(
                                    Required(
                                        item.audienceId,
                                        nameof(item.audienceId)),
                                    new GameEntityIdentity(
                                        Required(
                                            item.entityId,
                                            nameof(item.entityId)),
                                        item.incarnation));
                        }));
            }

            ProtocolValidator.EnsureValid(observation);
            return observation;
        }

        public static UnityActionRequestData ToUnity(ActionRequest value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            return new UnityActionRequestData
            {
                protocolVersion = value.ProtocolVersion,
                schemaVersion = value.SchemaVersion,
                extensionsJson = ExtensionsToJson(value.Extensions),
                operationId = value.OperationId,
                runId = value.RunId,
                turnId = value.TurnId,
                toolCallId = value.ToolCallId,
                agentId = value.AgentId,
                worldId = value.WorldId,
                actionName = value.ActionName,
                actionVersion = value.ActionVersion,
                argumentsJson = value.Arguments.GetRawText(),
                decisionKey = value.DecisionKey ?? string.Empty,
                batchId = value.BatchId ?? string.Empty,
                basedOnStateVersion = value.BasedOnStateVersion
                    ?? string.Empty,
                expectedEffects = value.ExpectedEffects.ToArray(),
                reasonCode = value.ReasonCode ?? string.Empty,
                hasRequestedAtUnixMilliseconds = true,
                requestedAtUnixMilliseconds =
                    value.RequestedAt.ToUnixTimeMilliseconds(),
                hasDeadlineUnixMilliseconds = value.Deadline.HasValue,
                deadlineUnixMilliseconds = value.Deadline.HasValue
                    ? value.Deadline.Value.ToUnixTimeMilliseconds()
                    : 0
            };
        }

        public static ActionReceipt ToProtocol(
            UnityActionReceiptData value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var ingressBudget = new JsonIngressBudget(
                MaximumDtoAggregateJsonUtf8Bytes,
                MaximumDtoAggregateJsonNodes);
            var receivedAt = RequiredUnixMilliseconds(
                value.receivedAtUnixMilliseconds,
                value.hasReceivedAtUnixMilliseconds,
                nameof(value.receivedAtUnixMilliseconds));
            var authoritativeObservations =
                value.authoritativeObservations
                ?? new UnityObservationData[0];
            if (authoritativeObservations.Length
                > ProtocolLimits.MaxAuthoritativeObservationsPerReceipt)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value.authoritativeObservations),
                    authoritativeObservations.Length,
                    "authoritativeObservations cannot exceed "
                    + ProtocolLimits.MaxAuthoritativeObservationsPerReceipt
                    + " items.");
            }

            var receiptExtensions = ParseExtensions(
                value.extensionsJson,
                ingressBudget,
                nameof(value.extensionsJson));
            var result = ParseNullableJson(
                value.resultJson,
                ingressBudget,
                nameof(value.resultJson));
            var stateDiff = ParseNullableJson(
                value.stateDiffJson,
                ingressBudget,
                nameof(value.stateDiffJson));
            var mappedObservations =
                new List<ObservationEnvelope>(
                    authoritativeObservations.Length);
            for (var index = 0;
                 index < authoritativeObservations.Length;
                 index++)
            {
                var observation = authoritativeObservations[index];
                if (observation == null)
                {
                    throw new ArgumentException(
                        "authoritativeObservations cannot contain null entries.",
                        nameof(value.authoritativeObservations));
                }

                mappedObservations.Add(
                    ToProtocol(observation, ingressBudget));
            }

            var receipt = new ActionReceipt
            {
                ProtocolVersion = Required(
                    value.protocolVersion,
                    nameof(value.protocolVersion)),
                SchemaVersion = Required(
                    value.schemaVersion,
                    nameof(value.schemaVersion)),
                Extensions = receiptExtensions,
                OperationId = Required(
                    value.operationId,
                    nameof(value.operationId)),
                Revision = value.revision,
                Status = Required(value.status, nameof(value.status)),
                Result = result,
                StateDiff = stateDiff,
                AuthoritativeObservations = mappedObservations,
                ErrorCode = EmptyToNull(value.errorCode),
                Retryable = value.retryable,
                CommittedAt = value.hasCommittedAtUnixMilliseconds
                              || value.committedAtUnixMilliseconds != 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(
                        value.committedAtUnixMilliseconds)
                    : (DateTimeOffset?)null,
                ReceivedAt = receivedAt
            };

            ProtocolValidator.EnsureValid(receipt);
            return receipt;
        }

        public static string ToJson(ObservationEnvelope value)
        {
            return ProtocolJson.Serialize(
                value ?? throw new ArgumentNullException(nameof(value)));
        }

        public static string ToJson(ActionRequest value)
        {
            return ProtocolJson.Serialize(
                value ?? throw new ArgumentNullException(nameof(value)));
        }

        public static string ToJson(ActionReceipt value)
        {
            return ProtocolJson.Serialize(
                value ?? throw new ArgumentNullException(nameof(value)));
        }

        public static string ToJson(RuntimeEvent value)
        {
            return ProtocolJson.Serialize(
                value ?? throw new ArgumentNullException(nameof(value)));
        }

        public static ObservationEnvelope ObservationFromJson(string json)
        {
            var ingress = ValidateBridgeDocument(
                json,
                nameof(json),
                BridgeDocumentShape.Observation);
            var value = ProtocolJson.DeserializeObservationEnvelope(
                ingress);
            ProtocolValidator.EnsureValid(value);
            return value;
        }

        public static ActionRequest ActionRequestFromJson(string json)
        {
            var ingress = ValidateBridgeDocument(
                json,
                nameof(json),
                BridgeDocumentShape.ActionRequest);
            var value = ProtocolJson.DeserializeActionRequest(
                ingress);
            ProtocolValidator.EnsureValid(value);
            return value;
        }

        public static ActionReceipt ActionReceiptFromJson(string json)
        {
            var ingress = ValidateBridgeDocument(
                json,
                nameof(json),
                BridgeDocumentShape.ActionReceipt);
            var value = ProtocolJson.DeserializeActionReceipt(
                ingress);
            ProtocolValidator.EnsureValid(value);
            return value;
        }

        public static RuntimeEvent RuntimeEventFromJson(string json)
        {
            var ingress = ValidateBridgeDocument(
                json,
                nameof(json),
                BridgeDocumentShape.RuntimeEvent);
            var value = ProtocolJson.DeserializeRuntimeEvent(
                ingress);
            ProtocolValidator.EnsureValid(value);
            return value;
        }

        private static JsonElement ParseOptionalJson(
            string json,
            string defaultJson,
            JsonIngressBudget ingressBudget,
            string field,
            bool requireRootObject = false,
            int maximumRootProperties =
                ProtocolLimits.MaxProtocolJsonContainerItems)
        {
            if (json != null
                && json.Length
                > ProtocolLimits.MaxProtocolJsonUtf8Bytes)
            {
                throw new JsonException(
                    field + " exceeds "
                    + ProtocolLimits.MaxProtocolJsonUtf8Bytes
                    + " UTF-8 bytes.");
            }

            var effectiveJson = string.IsNullOrWhiteSpace(json)
                ? defaultJson
                : json;
            ValidateJsonIngress(
                effectiveJson,
                field,
                ProtocolLimits.MaxProtocolJsonUtf8Bytes,
                ProtocolLimits.MaxProtocolJsonDepth,
                ProtocolLimits.MaxProtocolJsonNodes,
                ProtocolLimits.MaxProtocolJsonStringUtf8Bytes,
                ProtocolLimits.MaxProtocolJsonContainerItems,
                requireRootObject,
                maximumRootProperties,
                ingressBudget);
            return ProtocolJson.ParseElement(effectiveJson);
        }

        private static JsonElement? ParseNullableJson(
            string json,
            JsonIngressBudget ingressBudget,
            string field)
        {
            if (json == null || json.Length == 0)
            {
                return null;
            }

            if (json.Length > ProtocolLimits.MaxProtocolJsonUtf8Bytes)
            {
                throw new JsonException(
                    field + " exceeds "
                    + ProtocolLimits.MaxProtocolJsonUtf8Bytes
                    + " UTF-8 bytes.");
            }

            return string.IsNullOrWhiteSpace(json)
                ? (JsonElement?)null
                : ParseOptionalJson(
                    json,
                    "{}",
                    ingressBudget,
                    field);
        }

        private static Dictionary<string, JsonElement> ParseExtensions(
            string json,
            JsonIngressBudget ingressBudget,
            string field)
        {
            var value = ParseOptionalJson(
                json,
                "{}",
                ingressBudget,
                field,
                requireRootObject: true,
                maximumRootProperties:
                    ProtocolLimits.MaxProtocolExtensions);
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "extensionsJson must contain a JSON object.",
                    nameof(json));
            }

            var propertyCount = 0;
            foreach (var _ in value.EnumerateObject())
            {
                propertyCount++;
                if (propertyCount > ProtocolLimits.MaxProtocolExtensions)
                {
                    throw new JsonException(
                        "extensionsJson cannot exceed "
                        + ProtocolLimits.MaxProtocolExtensions
                        + " properties.");
                }
            }

            var result = new Dictionary<string, JsonElement>(
                propertyCount,
                StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!result.TryAdd(
                        property.Name,
                        property.Value.Clone()))
                {
                    throw new JsonException(
                        "extensionsJson contains duplicate properties.");
                }
            }

            return result;
        }

        private static string ExtensionsToJson(
            IReadOnlyDictionary<string, JsonElement> extensions)
        {
            if (extensions == null || extensions.Count == 0)
            {
                return "{}";
            }

            return GameAgent.Core.JsonArrayBuilder.Object(
                    extensions
                        .OrderBy(
                            pair => pair.Key,
                            StringComparer.Ordinal)
                        .Select(
                            pair => (
                                Name: pair.Key,
                                Value: pair.Value.Clone()))
                        .ToArray())
                .GetRawText();
        }

        private static string ValidateBridgeDocument(
            string json,
            string field,
            BridgeDocumentShape rootShape)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException(
                    field + " is required.",
                    field);
            }

            if (json.Length > MaximumBridgeDocumentUtf8Bytes)
            {
                throw new JsonException(
                    field + " exceeds "
                    + MaximumBridgeDocumentUtf8Bytes
                    + " UTF-8 bytes.");
            }

            ValidateJsonIngress(
                json,
                field,
                MaximumBridgeDocumentUtf8Bytes,
                MaximumBridgeDocumentJsonDepth,
                MaximumBridgeDocumentJsonNodes,
                ProtocolLimits.MaxProtocolJsonStringUtf8Bytes,
                ProtocolLimits.MaxProtocolJsonContainerItems,
                requireRootObject: true,
                maximumRootProperties:
                    ProtocolLimits.MaxProtocolJsonContainerItems,
                ingressBudget: null,
                rootShape);
            return json;
        }

        private static void ValidateJsonIngress(
            string json,
            string field,
            int maximumUtf8Bytes,
            int maximumDepth,
            int maximumNodes,
            int maximumStringUtf8Bytes,
            int maximumContainerItems,
            bool requireRootObject,
            int maximumRootProperties,
            JsonIngressBudget ingressBudget,
            BridgeDocumentShape rootShape =
                BridgeDocumentShape.Generic)
        {
            int utf8Bytes;
            try
            {
                utf8Bytes = StrictUtf8.GetByteCount(json);
            }
            catch (EncoderFallbackException)
            {
                throw new JsonException(
                    field + " contains invalid UTF-16.");
            }

            if (utf8Bytes > maximumUtf8Bytes)
            {
                throw new JsonException(
                    field + " exceeds "
                    + maximumUtf8Bytes
                    + " UTF-8 bytes.");
            }

            ingressBudget?.AdmitBytes(utf8Bytes, field);

            var utf8 = ArrayPool<byte>.Shared.Rent(
                Math.Max(1, utf8Bytes));
            try
            {
                var written = StrictUtf8.GetBytes(
                    json,
                    0,
                    json.Length,
                    utf8,
                    0);
                ValidateJsonTokens(
                    new ReadOnlySpan<byte>(utf8, 0, written),
                    field,
                    maximumDepth,
                    maximumNodes,
                    maximumStringUtf8Bytes,
                    maximumContainerItems,
                    requireRootObject,
                    maximumRootProperties,
                    ingressBudget,
                    rootShape);
            }
            catch (EncoderFallbackException)
            {
                throw new JsonException(
                    field + " contains invalid UTF-16.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(
                    utf8,
                    clearArray: true);
            }
        }

        private static void ValidateJsonTokens(
            ReadOnlySpan<byte> utf8,
            string field,
            int maximumDepth,
            int maximumNodes,
            int maximumStringUtf8Bytes,
            int maximumContainerItems,
            bool requireRootObject,
            int maximumRootProperties,
            JsonIngressBudget ingressBudget,
            BridgeDocumentShape rootShape)
        {
            var reader = new Utf8JsonReader(
                utf8,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = maximumDepth
                });
            var containerCounts = new int[maximumDepth + 1];
            var objectContainers = new bool[maximumDepth + 1];
            var containerLimits = new int[maximumDepth + 1];
            var containerShapes =
                new BridgeDocumentShape[maximumDepth + 1];
            var pendingPropertyNames = new string[maximumDepth + 1];
            var containerDepth = 0;
            var nodes = 0;
            var firstToken = true;

            while (reader.Read())
            {
                if (firstToken)
                {
                    firstToken = false;
                    if (requireRootObject
                        && reader.TokenType != JsonTokenType.StartObject)
                    {
                        throw new JsonException(
                            field + " must contain a JSON object.");
                    }
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        AdmitArrayValue(
                            containerCounts,
                            objectContainers,
                            containerLimits,
                            containerDepth,
                            field);
                        nodes++;
                        EnsureNodeLimit(nodes, maximumNodes, field);
                        if (containerDepth >= maximumDepth)
                        {
                            throw new JsonException(
                                field + " exceeds JSON depth "
                                + maximumDepth
                                + ".");
                        }

                        var isObject =
                            reader.TokenType == JsonTokenType.StartObject;
                        var shape = containerDepth == 0
                            ? rootShape
                            : ClassifyBridgeChild(
                                containerShapes[containerDepth - 1],
                                pendingPropertyNames[
                                    containerDepth - 1],
                                isObject);
                        if (containerDepth > 0
                            && objectContainers[containerDepth - 1])
                        {
                            pendingPropertyNames[
                                containerDepth - 1] = null;
                        }

                        containerCounts[containerDepth] = 0;
                        objectContainers[containerDepth] = isObject;
                        containerShapes[containerDepth] = shape;
                        containerLimits[containerDepth] =
                            GetBridgeContainerLimit(
                                shape,
                                containerDepth == 0
                                    ? maximumRootProperties
                                    : maximumContainerItems);
                        pendingPropertyNames[containerDepth] = null;
                        containerDepth++;
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        containerDepth--;
                        if (containerDepth < 0)
                        {
                            throw new JsonException(
                                field + " contains invalid JSON.");
                        }

                        break;

                    case JsonTokenType.PropertyName:
                        if (containerDepth == 0
                            || !objectContainers[containerDepth - 1])
                        {
                            throw new JsonException(
                                field + " contains invalid JSON.");
                        }

                        containerCounts[containerDepth - 1]++;
                        EnsureContainerLimit(
                            containerCounts[containerDepth - 1],
                            containerLimits[containerDepth - 1],
                            field);
                        nodes++;
                        EnsureNodeLimit(nodes, maximumNodes, field);
                        EnsureEncodedStringTokenLimit(
                            reader,
                            maximumStringUtf8Bytes,
                            field);
                        var propertyName = reader.GetString();
                        EnsureStringLimit(
                            propertyName,
                            maximumStringUtf8Bytes,
                            field);
                        pendingPropertyNames[containerDepth - 1] =
                            propertyName;
                        break;

                    case JsonTokenType.String:
                        AdmitArrayValue(
                            containerCounts,
                            objectContainers,
                            containerLimits,
                            containerDepth,
                            field);
                        nodes++;
                        EnsureNodeLimit(nodes, maximumNodes, field);
                        EnsureEncodedStringTokenLimit(
                            reader,
                            maximumStringUtf8Bytes,
                            field);
                        EnsureStringLimit(
                            reader.GetString(),
                            maximumStringUtf8Bytes,
                            field);
                        break;

                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        AdmitArrayValue(
                            containerCounts,
                            objectContainers,
                            containerLimits,
                            containerDepth,
                            field);
                        nodes++;
                        EnsureNodeLimit(nodes, maximumNodes, field);
                        break;
                }
            }

            if (containerDepth != 0 || nodes == 0)
            {
                throw new JsonException(
                    field + " must contain one complete JSON value.");
            }

            ingressBudget?.AdmitNodes(nodes, field);
        }

        private static void AdmitArrayValue(
            int[] containerCounts,
            bool[] objectContainers,
            int[] containerLimits,
            int containerDepth,
            string field)
        {
            if (containerDepth == 0
                || objectContainers[containerDepth - 1])
            {
                return;
            }

            containerCounts[containerDepth - 1]++;
            EnsureContainerLimit(
                containerCounts[containerDepth - 1],
                containerLimits[containerDepth - 1],
                field);
        }

        private static BridgeDocumentShape ClassifyBridgeChild(
            BridgeDocumentShape parentShape,
            string propertyName,
            bool isObject)
        {
            if (parentShape
                    == BridgeDocumentShape.AuthoritativeObservations
                && isObject)
            {
                return BridgeDocumentShape.Observation;
            }

            if (isObject)
            {
                if (IsVersionedDocumentShape(parentShape)
                    && string.Equals(
                        propertyName,
                        "extensions",
                        StringComparison.Ordinal))
                {
                    return BridgeDocumentShape.Extensions;
                }

                if (parentShape == BridgeDocumentShape.Observation
                    && string.Equals(
                        propertyName,
                        "visibility",
                        StringComparison.Ordinal))
                {
                    return BridgeDocumentShape.Visibility;
                }

                return BridgeDocumentShape.Generic;
            }

            if (parentShape == BridgeDocumentShape.Observation
                && string.Equals(
                    propertyName,
                    "subjectIds",
                    StringComparison.Ordinal))
            {
                return BridgeDocumentShape.SubjectIds;
            }

            if (parentShape == BridgeDocumentShape.Visibility
                && string.Equals(
                    propertyName,
                    "audienceIds",
                    StringComparison.Ordinal))
            {
                return BridgeDocumentShape.AudienceIds;
            }

            if (parentShape == BridgeDocumentShape.ActionRequest
                && string.Equals(
                    propertyName,
                    "expectedEffects",
                    StringComparison.Ordinal))
            {
                return BridgeDocumentShape.ExpectedEffects;
            }

            if (parentShape == BridgeDocumentShape.ActionReceipt
                && string.Equals(
                    propertyName,
                    "authoritativeObservations",
                    StringComparison.Ordinal))
            {
                return BridgeDocumentShape.AuthoritativeObservations;
            }

            return BridgeDocumentShape.Generic;
        }

        private static bool IsVersionedDocumentShape(
            BridgeDocumentShape shape)
        {
            return shape == BridgeDocumentShape.Observation
                   || shape == BridgeDocumentShape.ActionRequest
                   || shape == BridgeDocumentShape.ActionReceipt
                   || shape == BridgeDocumentShape.RuntimeEvent;
        }

        private static int GetBridgeContainerLimit(
            BridgeDocumentShape shape,
            int fallback)
        {
            switch (shape)
            {
                case BridgeDocumentShape.Extensions:
                    return ProtocolLimits.MaxProtocolExtensions;
                case BridgeDocumentShape.AuthoritativeObservations:
                    return ProtocolLimits
                        .MaxAuthoritativeObservationsPerReceipt;
                case BridgeDocumentShape.ExpectedEffects:
                    return ProtocolLimits.MaxActionExpectedEffects;
                case BridgeDocumentShape.SubjectIds:
                    return ProtocolLimits.MaxObservationSubjectIds;
                case BridgeDocumentShape.AudienceIds:
                    return ProtocolLimits.MaxObservationAudienceIds;
                default:
                    return fallback;
            }
        }

        private static void EnsureNodeLimit(
            int nodes,
            int maximumNodes,
            string field)
        {
            if (nodes > maximumNodes)
            {
                throw new JsonException(
                    field + " exceeds "
                    + maximumNodes
                    + " JSON nodes.");
            }
        }

        private static void EnsureContainerLimit(
            int items,
            int maximumItems,
            string field)
        {
            if (items > maximumItems)
            {
                throw new JsonException(
                    field + " contains a JSON container over "
                    + maximumItems
                    + " items.");
            }
        }

        private static void EnsureStringLimit(
            string value,
            int maximumUtf8Bytes,
            string field)
        {
            var utf8Bytes = StrictUtf8.GetByteCount(value ?? string.Empty);
            if (utf8Bytes > maximumUtf8Bytes)
            {
                throw new JsonException(
                    field + " contains a JSON string over "
                    + maximumUtf8Bytes
                    + " UTF-8 bytes.");
            }
        }

        private static void EnsureEncodedStringTokenLimit(
            Utf8JsonReader reader,
            int maximumUtf8Bytes,
            string field)
        {
            var encodedBytes = reader.HasValueSequence
                ? reader.ValueSequence.Length
                : reader.ValueSpan.Length;
            if (encodedBytes > (long)maximumUtf8Bytes * 6)
            {
                throw new JsonException(
                    field + " contains an encoded JSON string over the "
                    + "bounded decode allocation limit.");
            }
        }

        private static DateTimeOffset RequiredUnixMilliseconds(
            long value,
            bool hasValue,
            string field)
        {
            if (!hasValue && value == 0)
            {
                throw new ArgumentException(field + " is required.", field);
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }

        private static bool IsPresentNonNegative(
            long value,
            bool hasValue,
            long absentSentinel,
            string field)
        {
            if (value < 0 && (hasValue || value != absentSentinel))
            {
                throw new ArgumentOutOfRangeException(
                    field,
                    value,
                    field + " must not be negative when present.");
            }

            return hasValue || value != absentSentinel;
        }

        private static string Required(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(field + " is required.", field);
            }

            return value;
        }

        private static string EmptyToNull(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static List<string> Copy(
            string[] values,
            int maximumItems,
            string field)
        {
            if (values == null)
            {
                return new List<string>();
            }

            if (values.Length > maximumItems)
            {
                throw new ArgumentOutOfRangeException(
                    field,
                    values.Length,
                    field + " cannot exceed "
                    + maximumItems
                    + " items.");
            }

            return new List<string>(values);
        }

        private sealed class JsonIngressBudget
        {
            private readonly int _maximumUtf8Bytes;
            private readonly int _maximumNodes;
            private long _utf8Bytes;
            private long _nodes;

            internal JsonIngressBudget(
                int maximumUtf8Bytes,
                int maximumNodes)
            {
                _maximumUtf8Bytes = maximumUtf8Bytes;
                _maximumNodes = maximumNodes;
            }

            internal void AdmitBytes(int utf8Bytes, string field)
            {
                _utf8Bytes += utf8Bytes;
                if (_utf8Bytes > _maximumUtf8Bytes)
                {
                    throw new JsonException(
                        field + " exceeds the DTO aggregate JSON budget of "
                        + _maximumUtf8Bytes
                        + " UTF-8 bytes.");
                }
            }

            internal void AdmitNodes(int nodes, string field)
            {
                _nodes += nodes;
                if (_nodes > _maximumNodes)
                {
                    throw new JsonException(
                        field + " exceeds the DTO aggregate JSON budget of "
                        + _maximumNodes
                        + " nodes.");
                }
            }
        }
    }
}
