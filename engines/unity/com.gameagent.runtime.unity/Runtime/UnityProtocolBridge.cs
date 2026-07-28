using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GameAgent.Protocol;
using UnityEngine.Scripting;

namespace GameAgent.Unity
{
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
        public long resourceSizeBytes = -1;
        public bool hasObservedAtUnixMilliseconds;
        public long observedAtUnixMilliseconds;
        public bool hasTtlMilliseconds;
        public long ttlMilliseconds;
        public long sequence = -1;
        public string stateVersion = string.Empty;
        public string trust = "untrusted";
        public string visibilityScope = "world";
        public string[] audienceIds = new string[0];
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
        public static ObservationEnvelope ToProtocol(
            UnityObservationData value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var observation = new ObservationEnvelope
            {
                ProtocolVersion = Required(
                    value.protocolVersion,
                    nameof(value.protocolVersion)),
                SchemaVersion = Required(
                    value.schemaVersion,
                    nameof(value.schemaVersion)),
                Extensions = ParseExtensions(value.extensionsJson),
                ObservationId = Required(
                    value.observationId,
                    nameof(value.observationId)),
                WorldId = Required(value.worldId, nameof(value.worldId)),
                SessionId = EmptyToNull(value.sessionId),
                Source = Required(value.source, nameof(value.source)),
                Kind = Required(value.kind, nameof(value.kind)),
                SubjectIds = Copy(value.subjectIds),
                ContentType = Required(
                    value.contentType,
                    nameof(value.contentType)),
                SchemaRef = EmptyToNull(value.schemaRef),
                ContentSchemaVersion =
                    EmptyToNull(value.contentSchemaVersion),
                ObservedAt = FromUnixMillisecondsOrNow(
                    value.observedAtUnixMilliseconds,
                    value.hasObservedAtUnixMilliseconds),
                TtlMs = value.hasTtlMilliseconds
                        || value.ttlMilliseconds != 0
                    ? value.ttlMilliseconds
                    : (long?)null,
                Sequence = value.sequence >= 0
                    ? value.sequence
                    : (long?)null,
                StateVersion = EmptyToNull(value.stateVersion),
                Trust = Required(value.trust, nameof(value.trust)),
                Visibility = new VisibilityRule
                {
                    Scope = Required(
                        value.visibilityScope,
                        nameof(value.visibilityScope)),
                    AudienceIds = Copy(value.audienceIds)
                },
                Priority = value.priority,
                CacheKey = EmptyToNull(value.cacheKey)
            };

            if (!string.IsNullOrWhiteSpace(value.resourceUri))
            {
                observation.ResourceRef = new ResourceReference
                {
                    Uri = value.resourceUri,
                    MediaType = Required(
                        value.resourceMediaType,
                        nameof(value.resourceMediaType)),
                    Digest = EmptyToNull(value.resourceDigest),
                    SizeBytes = value.resourceSizeBytes >= 0
                        ? value.resourceSizeBytes
                        : (long?)null
                };
            }
            else
            {
                observation.Payload = ParseOptionalJson(
                    value.payloadJson,
                    defaultJson: "{}");
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

            var receipt = new ActionReceipt
            {
                ProtocolVersion = Required(
                    value.protocolVersion,
                    nameof(value.protocolVersion)),
                SchemaVersion = Required(
                    value.schemaVersion,
                    nameof(value.schemaVersion)),
                Extensions = ParseExtensions(value.extensionsJson),
                OperationId = Required(
                    value.operationId,
                    nameof(value.operationId)),
                Revision = value.revision,
                Status = Required(value.status, nameof(value.status)),
                Result = ParseNullableJson(value.resultJson),
                StateDiff = ParseNullableJson(value.stateDiffJson),
                AuthoritativeObservations =
                    (value.authoritativeObservations
                     ?? new UnityObservationData[0])
                    .Select(ToProtocol)
                    .ToList(),
                ErrorCode = EmptyToNull(value.errorCode),
                Retryable = value.retryable,
                CommittedAt = value.hasCommittedAtUnixMilliseconds
                              || value.committedAtUnixMilliseconds != 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(
                        value.committedAtUnixMilliseconds)
                    : (DateTimeOffset?)null,
                ReceivedAt = FromUnixMillisecondsOrNow(
                    value.receivedAtUnixMilliseconds,
                    value.hasReceivedAtUnixMilliseconds)
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
            var value = ProtocolJson.DeserializeObservationEnvelope(
                Required(json, nameof(json)));
            ProtocolValidator.EnsureValid(value);
            return value;
        }

        public static ActionRequest ActionRequestFromJson(string json)
        {
            var value = ProtocolJson.DeserializeActionRequest(
                Required(json, nameof(json)));
            ProtocolValidator.EnsureValid(value);
            return value;
        }

        public static ActionReceipt ActionReceiptFromJson(string json)
        {
            var value = ProtocolJson.DeserializeActionReceipt(
                Required(json, nameof(json)));
            ProtocolValidator.EnsureValid(value);
            return value;
        }

        public static RuntimeEvent RuntimeEventFromJson(string json)
        {
            return ProtocolJson.DeserializeRuntimeEvent(
                Required(json, nameof(json)));
        }

        private static JsonElement ParseOptionalJson(
            string json,
            string defaultJson)
        {
            return ProtocolJson.ParseElement(
                string.IsNullOrWhiteSpace(json) ? defaultJson : json);
        }

        private static JsonElement? ParseNullableJson(string json)
        {
            return string.IsNullOrWhiteSpace(json)
                ? (JsonElement?)null
                : ProtocolJson.ParseElement(json);
        }

        private static Dictionary<string, JsonElement> ParseExtensions(
            string json)
        {
            var value = ParseOptionalJson(json, "{}");
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "extensionsJson must contain a JSON object.",
                    nameof(json));
            }

            return value
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.Ordinal);
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

        private static DateTimeOffset FromUnixMillisecondsOrNow(
            long value,
            bool hasValue)
        {
            return hasValue || value != 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.UtcNow;
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

        private static List<string> Copy(string[] values)
        {
            return values == null
                ? new List<string>()
                : new List<string>(values);
        }
    }
}
