using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

internal static class ActionReceiptIngressValidator
{
    internal const int MaxAuthoritativeObservations = 64;
    internal const int MaxReceiptUtf8Bytes = 262_144;

    private const int MaxCollectionItems = 256;
    private const int MaxExtensionItems = 64;

    private static readonly JsonValueLimits FieldLimits = new(
        maxUtf8Bytes: MaxReceiptUtf8Bytes,
        maxDepth: 32,
        maxNodes: 8_192,
        maxStringUtf8Bytes: 65_536,
        maxContainerItems: 2_048);

    public static ActionReceipt ValidateAndClone(
        ActionRequest expectedRequest,
        ActionReceipt hostReceipt)
    {
        if (expectedRequest is null)
        {
            throw new ArgumentNullException(nameof(expectedRequest));
        }

        if (hostReceipt is null)
        {
            throw new InvalidDataException(
                "The game host returned a null action receipt.");
        }

        if (!string.Equals(
                hostReceipt.OperationId,
                expectedRequest.OperationId,
                StringComparison.Ordinal))
        {
            throw new OperationLedgerConflictException(
                expectedRequest.OperationId,
                "the host returned a receipt for a different operation.");
        }

        hostReceipt = SnapshotHostReceipt(hostReceipt);
        ProtocolValidator.EnsureValid(expectedRequest);
        ProtocolValidator.EnsureValid(hostReceipt);

        long boundedBytes = 0;
        AddOptionalString(
            hostReceipt.ErrorCode,
            128,
            nameof(hostReceipt.ErrorCode),
            ref boundedBytes);
        AddJson(
            hostReceipt.Result,
            nameof(hostReceipt.Result),
            ref boundedBytes);
        AddJson(
            hostReceipt.StateDiff,
            nameof(hostReceipt.StateDiff),
            ref boundedBytes);
        AddExtensions(
            hostReceipt.Extensions,
            nameof(hostReceipt.Extensions),
            ref boundedBytes);

        if (hostReceipt.AuthoritativeObservations is null)
        {
            throw new JsonException(
                "$.authoritativeObservations: A collection is required.");
        }

        if (hostReceipt.AuthoritativeObservations.Count
            > MaxAuthoritativeObservations)
        {
            throw LimitExceeded(
                nameof(hostReceipt.AuthoritativeObservations),
                "authoritative_observation_count_exceeded",
                $"An action receipt cannot contain more than "
                + $"{MaxAuthoritativeObservations} authoritative observations.");
        }

        long observationBytes = 0;
        foreach (var observation in hostReceipt.AuthoritativeObservations)
        {
            ValidateObservationBeforeSerialization(
                expectedRequest,
                observation);
            var encoded = ProtocolJson.ToElement(observation);
            var encodedBytes = JsonValueInspector.ValidateAndMeasure(
                encoded,
                FieldLimits,
                nameof(hostReceipt.AuthoritativeObservations));
            AddBytes(
                ref observationBytes,
                encodedBytes,
                nameof(hostReceipt.AuthoritativeObservations),
                "authoritative_observation_bytes_exceeded");
            AddBytes(
                ref boundedBytes,
                encodedBytes,
                nameof(hostReceipt),
                "action_receipt_bytes_exceeded");
        }

        AddRequiredString(
            hostReceipt.OperationId,
            128,
            nameof(hostReceipt.OperationId),
            ref boundedBytes);
        AddRequiredString(
            hostReceipt.Status,
            32,
            nameof(hostReceipt.Status),
            ref boundedBytes);
        AddRequiredString(
            hostReceipt.ProtocolVersion,
            16,
            nameof(hostReceipt.ProtocolVersion),
            ref boundedBytes);
        AddRequiredString(
            hostReceipt.SchemaVersion,
            16,
            nameof(hostReceipt.SchemaVersion),
            ref boundedBytes);

        var serialized = ProtocolJson.Serialize(hostReceipt);
        if (Encoding.UTF8.GetByteCount(serialized) > MaxReceiptUtf8Bytes)
        {
            throw LimitExceeded(
                nameof(hostReceipt),
                "action_receipt_bytes_exceeded",
                $"An action receipt cannot exceed "
                + $"{MaxReceiptUtf8Bytes} UTF-8 bytes.");
        }

        return ProtocolJson.DeserializeActionReceipt(serialized);
    }

    private static ActionReceipt SnapshotHostReceipt(
        ActionReceipt hostReceipt)
    {
        var sourceObservations =
            hostReceipt.AuthoritativeObservations;
        if (sourceObservations is null)
        {
            throw new JsonException(
                "$.authoritativeObservations: A collection is required.");
        }

        var observationCount = sourceObservations.Count;
        if (observationCount > MaxAuthoritativeObservations)
        {
            throw LimitExceeded(
                nameof(hostReceipt.AuthoritativeObservations),
                "authoritative_observation_count_exceeded",
                $"An action receipt cannot contain more than "
                + $"{MaxAuthoritativeObservations} authoritative observations.");
        }

        var observations =
            new List<ObservationEnvelope>(observationCount);
        for (var index = 0; index < observationCount; index++)
        {
            var observation = sourceObservations[index]
                ?? throw new JsonException(
                    "$.authoritativeObservations: "
                    + "Null observations are not allowed.");
            observations.Add(
                RuntimeProtocolInputGuard
                    .ValidateObservationBeforeSerialization(
                        observation,
                        FieldLimits,
                        MaxReceiptUtf8Bytes,
                        nameof(hostReceipt.AuthoritativeObservations),
                        maximumExtensionItems: MaxExtensionItems,
                        byteLimitCode:
                            "authoritative_observation_bytes_exceeded",
                        itemLimitCode:
                            "collection_items_exceeded"));
        }

        return new ActionReceipt
        {
            ProtocolVersion = hostReceipt.ProtocolVersion,
            SchemaVersion = hostReceipt.SchemaVersion,
            Extensions = SnapshotExtensions(hostReceipt.Extensions)!,
            OperationId = hostReceipt.OperationId,
            Revision = hostReceipt.Revision,
            Status = hostReceipt.Status,
            Result = hostReceipt.Result,
            StateDiff = hostReceipt.StateDiff,
            AuthoritativeObservations = observations,
            ErrorCode = hostReceipt.ErrorCode,
            Retryable = hostReceipt.Retryable,
            CommittedAt = hostReceipt.CommittedAt,
            ReceivedAt = hostReceipt.ReceivedAt
        };
    }

    private static Dictionary<string, JsonElement>? SnapshotExtensions(
        Dictionary<string, JsonElement>? extensions)
    {
        if (extensions is null)
        {
            return null;
        }

        var count = extensions.Count;
        if (count > MaxExtensionItems)
        {
            throw LimitExceeded(
                nameof(extensions),
                "extension_items_exceeded",
                $"Extensions cannot contain more than "
                + $"{MaxExtensionItems} items.");
        }

        var snapshot = new Dictionary<string, JsonElement>(
            count,
            StringComparer.Ordinal);
        var enumerator = extensions.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (snapshot.Count >= MaxExtensionItems)
            {
                throw LimitExceeded(
                    nameof(extensions),
                    "extension_items_exceeded",
                    $"Extensions cannot contain more than "
                    + $"{MaxExtensionItems} items.");
            }

            var pair = enumerator.Current;
            snapshot.Add(pair.Key, pair.Value);
        }

        return snapshot;
    }

    private static void ValidateObservationBeforeSerialization(
        ActionRequest expectedRequest,
        ObservationEnvelope? observation)
    {
        if (observation is null)
        {
            throw new JsonException(
                "$.authoritativeObservations: Null observations are not allowed.");
        }

        if (observation.Visibility is null)
        {
            throw new JsonException(
                "$.authoritativeObservations[].visibility: "
                + "A visibility rule is required.");
        }

        long boundedBytes = 0;
        AddRequiredId(
            observation.ObservationId,
            nameof(observation.ObservationId),
            ref boundedBytes);
        AddRequiredId(
            observation.WorldId,
            nameof(observation.WorldId),
            ref boundedBytes);
        if (!string.Equals(
                observation.WorldId,
                expectedRequest.WorldId,
                StringComparison.Ordinal))
        {
            throw new OperationLedgerConflictException(
                expectedRequest.OperationId,
                "an authoritative observation belongs to a different world.");
        }

        AddOptionalId(
            observation.SessionId,
            nameof(observation.SessionId),
            ref boundedBytes);
        AddRequiredString(
            observation.Source,
            128,
            nameof(observation.Source),
            ref boundedBytes);
        AddRequiredString(
            observation.Kind,
            64,
            nameof(observation.Kind),
            ref boundedBytes);
        AddRequiredString(
            observation.ContentType,
            128,
            nameof(observation.ContentType),
            ref boundedBytes);
        AddOptionalString(
            observation.SchemaRef,
            4_096,
            nameof(observation.SchemaRef),
            ref boundedBytes);
        AddOptionalString(
            observation.ContentSchemaVersion,
            32,
            nameof(observation.ContentSchemaVersion),
            ref boundedBytes);
        AddOptionalString(
            observation.StateVersion,
            128,
            nameof(observation.StateVersion),
            ref boundedBytes);
        AddRequiredString(
            observation.Trust,
            64,
            nameof(observation.Trust),
            ref boundedBytes);
        AddOptionalString(
            observation.CacheKey,
            256,
            nameof(observation.CacheKey),
            ref boundedBytes);
        AddRequiredString(
            observation.ProtocolVersion,
            16,
            nameof(observation.ProtocolVersion),
            ref boundedBytes);
        AddRequiredString(
            observation.SchemaVersion,
            16,
            nameof(observation.SchemaVersion),
            ref boundedBytes);
        AddIds(
            observation.SubjectIds,
            nameof(observation.SubjectIds),
            ref boundedBytes);
        AddRequiredString(
            observation.Visibility.Scope,
            64,
            nameof(observation.Visibility.Scope),
            ref boundedBytes);
        AddIds(
            observation.Visibility.AudienceIds,
            nameof(observation.Visibility.AudienceIds),
            ref boundedBytes);
        AddJson(
            observation.Payload,
            nameof(observation.Payload),
            ref boundedBytes);
        AddExtensions(
            observation.Extensions,
            nameof(observation.Extensions),
            ref boundedBytes);

        if (observation.ResourceRef is not null)
        {
            AddRequiredString(
                observation.ResourceRef.Uri,
                4_096,
                nameof(observation.ResourceRef.Uri),
                ref boundedBytes);
            AddRequiredString(
                observation.ResourceRef.MediaType,
                128,
                nameof(observation.ResourceRef.MediaType),
                ref boundedBytes);
            AddOptionalString(
                observation.ResourceRef.Digest,
                256,
                nameof(observation.ResourceRef.Digest),
                ref boundedBytes);
        }

        ProtocolValidator.EnsureValid(observation);
    }

    private static void AddIds(
        IReadOnlyCollection<string>? values,
        string parameterName,
        ref long total)
    {
        if (values is null)
        {
            throw new JsonException(
                $"$.{parameterName}: A collection is required.");
        }

        if (values.Count > MaxCollectionItems)
        {
            throw LimitExceeded(
                parameterName,
                "collection_items_exceeded",
                $"The collection cannot contain more than "
                + $"{MaxCollectionItems} items.");
        }

        foreach (var value in values)
        {
            AddRequiredId(value, parameterName, ref total);
        }
    }

    private static void AddExtensions(
        IReadOnlyDictionary<string, JsonElement>? extensions,
        string parameterName,
        ref long total)
    {
        if (extensions is null)
        {
            throw new JsonException(
                $"$.{parameterName}: An object is required.");
        }

        if (extensions.Count > MaxExtensionItems)
        {
            throw LimitExceeded(
                parameterName,
                "extension_items_exceeded",
                $"Extensions cannot contain more than "
                + $"{MaxExtensionItems} items.");
        }

        foreach (var pair in extensions)
        {
            AddRequiredString(
                pair.Key,
                128,
                parameterName,
                ref total);
            var bytes = JsonValueInspector.ValidateAndMeasure(
                pair.Value,
                FieldLimits,
                parameterName);
            AddBytes(
                ref total,
                bytes,
                parameterName,
                "action_receipt_bytes_exceeded");
        }
    }

    private static void AddJson(
        JsonElement? value,
        string parameterName,
        ref long total)
    {
        if (!value.HasValue)
        {
            return;
        }

        var bytes = JsonValueInspector.ValidateAndMeasure(
            value.Value,
            FieldLimits,
            parameterName);
        AddBytes(
            ref total,
            bytes,
            parameterName,
            "action_receipt_bytes_exceeded");
    }

    private static void AddRequiredId(
        string? value,
        string parameterName,
        ref long total)
    {
        var validated = RuntimeGuard.RequiredId(value, parameterName);
        AddBytes(
            ref total,
            Encoding.UTF8.GetByteCount(validated),
            parameterName,
            "action_receipt_bytes_exceeded");
    }

    private static void AddOptionalId(
        string? value,
        string parameterName,
        ref long total)
    {
        if (value is null)
        {
            return;
        }

        AddRequiredId(value, parameterName, ref total);
    }

    private static void AddRequiredString(
        string? value,
        int maxUtf8Bytes,
        string parameterName,
        ref long total)
    {
        var validated = RuntimeGuard.RequiredUtf8(
            value,
            maxUtf8Bytes,
            parameterName);
        AddBytes(
            ref total,
            Encoding.UTF8.GetByteCount(validated),
            parameterName,
            "action_receipt_bytes_exceeded");
    }

    private static void AddOptionalString(
        string? value,
        int maxUtf8Bytes,
        string parameterName,
        ref long total)
    {
        if (value is null)
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes)
        {
            throw LimitExceeded(
                parameterName,
                "string_bytes_exceeded",
                $"The value cannot exceed {maxUtf8Bytes} UTF-8 bytes.");
        }

        AddBytes(
            ref total,
            Encoding.UTF8.GetByteCount(value),
            parameterName,
            "action_receipt_bytes_exceeded");
    }

    private static void AddBytes(
        ref long total,
        long bytes,
        string parameterName,
        string code)
    {
        if (bytes < 0 || total > MaxReceiptUtf8Bytes - bytes)
        {
            throw LimitExceeded(
                parameterName,
                code,
                $"The bounded receipt content cannot exceed "
                + $"{MaxReceiptUtf8Bytes} UTF-8 bytes.");
        }

        total += bytes;
    }

    private static RuntimeContentLimitException LimitExceeded(
        string parameterName,
        string code,
        string message)
    {
        return new RuntimeContentLimitException(
            parameterName,
            code,
            message);
    }
}
