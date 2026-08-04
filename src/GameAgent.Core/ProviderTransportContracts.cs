using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public enum ProviderRequestFamily
{
    Custom = 0,
    ChatCompletions = 1,
    Responses = 2,
    Interactions = 3
}

public enum ProviderStreamFraming
{
    Custom = 0,
    ServerSentEvents = 1
}

/// <summary>
/// Describes the versioned wire semantics of a provider route. This contract
/// identifies behavior only; endpoint, pricing, and route-specific limits
/// remain part of the route policy.
/// </summary>
public sealed class ProviderDialectContract
{
    public const string JournalExtensionName =
        "providerDialectContract";

    public const string CurrentContractVersion =
        "provider-dialect-contract.v1";

    public ProviderDialectContract(
        string identifier,
        ProviderRequestFamily requestFamily,
        string requestSchemaVersion,
        ProviderStreamFraming streamFraming,
        string streamFramingVersion,
        string toolCallSemanticsVersion,
        string usageSemanticsVersion,
        string reasoningSemanticsVersion,
        string requestContentType,
        string? opaqueContinuationStateVersion = null,
        string contractVersion = CurrentContractVersion)
    {
        Identifier = RuntimeGuard.RequiredUtf8(
            identifier,
            128,
            nameof(identifier));
        if (!Enum.IsDefined(typeof(ProviderRequestFamily), requestFamily))
        {
            throw new ArgumentOutOfRangeException(nameof(requestFamily));
        }

        if (!Enum.IsDefined(typeof(ProviderStreamFraming), streamFraming))
        {
            throw new ArgumentOutOfRangeException(nameof(streamFraming));
        }

        RequestFamily = requestFamily;
        RequestSchemaVersion = RuntimeGuard.RequiredUtf8(
            requestSchemaVersion,
            128,
            nameof(requestSchemaVersion));
        StreamFraming = streamFraming;
        StreamFramingVersion = RuntimeGuard.RequiredUtf8(
            streamFramingVersion,
            128,
            nameof(streamFramingVersion));
        ToolCallSemanticsVersion = RuntimeGuard.RequiredUtf8(
            toolCallSemanticsVersion,
            128,
            nameof(toolCallSemanticsVersion));
        UsageSemanticsVersion = RuntimeGuard.RequiredUtf8(
            usageSemanticsVersion,
            128,
            nameof(usageSemanticsVersion));
        ReasoningSemanticsVersion = RuntimeGuard.RequiredUtf8(
            reasoningSemanticsVersion,
            128,
            nameof(reasoningSemanticsVersion));
        RequestContentType = RuntimeGuard.RequiredUtf8(
            requestContentType,
            128,
            nameof(requestContentType));
        OpaqueContinuationStateVersion =
            opaqueContinuationStateVersion is null
                ? null
                : RuntimeGuard.RequiredUtf8(
                    opaqueContinuationStateVersion,
                    128,
                    nameof(opaqueContinuationStateVersion));
        ContractVersion = RuntimeGuard.RequiredUtf8(
            contractVersion,
            128,
            nameof(contractVersion));

        var digest = new CanonicalDigestBuilder();
        digest.Add("type", ContractVersion);
        digest.Add("identifier", Identifier);
        digest.Add(
            "requestFamily",
            ((int)RequestFamily).ToString(CultureInfo.InvariantCulture));
        digest.Add("requestSchemaVersion", RequestSchemaVersion);
        digest.Add(
            "streamFraming",
            ((int)StreamFraming).ToString(CultureInfo.InvariantCulture));
        digest.Add("streamFramingVersion", StreamFramingVersion);
        digest.Add("toolCallSemanticsVersion", ToolCallSemanticsVersion);
        digest.Add("usageSemanticsVersion", UsageSemanticsVersion);
        digest.Add("reasoningSemanticsVersion", ReasoningSemanticsVersion);
        digest.Add("requestContentType", RequestContentType);
        digest.Add(
            "opaqueContinuationStateVersion",
            OpaqueContinuationStateVersion ?? "unsupported");
        SemanticDigest = digest.Finish();
    }

    public string Identifier { get; }

    public ProviderRequestFamily RequestFamily { get; }

    public string RequestSchemaVersion { get; }

    public ProviderStreamFraming StreamFraming { get; }

    public string StreamFramingVersion { get; }

    public string ToolCallSemanticsVersion { get; }

    public string UsageSemanticsVersion { get; }

    public string ReasoningSemanticsVersion { get; }

    public string RequestContentType { get; }

    public string? OpaqueContinuationStateVersion { get; }

    public bool SupportsOpaqueContinuationState =>
        OpaqueContinuationStateVersion is not null;

    public string ContractVersion { get; }

    public string SemanticDigest { get; }

    public static ProviderDialectContract LegacyCustom(string identifier)
    {
        return new ProviderDialectContract(
            identifier,
            ProviderRequestFamily.Custom,
            "custom.request.unspecified.v1",
            ProviderStreamFraming.Custom,
            "custom.stream.unspecified.v1",
            "custom.tools.unspecified.v1",
            "custom.usage.unspecified.v1",
            "custom.reasoning.unspecified.v1",
            "application/octet-stream");
    }

    public JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("contractVersion", JsonArrayBuilder.String(ContractVersion)),
            ("identifier", JsonArrayBuilder.String(Identifier)),
            ("requestFamily",
                JsonArrayBuilder.Number((int)RequestFamily)),
            ("requestSchemaVersion",
                JsonArrayBuilder.String(RequestSchemaVersion)),
            ("streamFraming",
                JsonArrayBuilder.Number((int)StreamFraming)),
            ("streamFramingVersion",
                JsonArrayBuilder.String(StreamFramingVersion)),
            ("toolCallSemanticsVersion",
                JsonArrayBuilder.String(ToolCallSemanticsVersion)),
            ("usageSemanticsVersion",
                JsonArrayBuilder.String(UsageSemanticsVersion)),
            ("reasoningSemanticsVersion",
                JsonArrayBuilder.String(ReasoningSemanticsVersion)),
            ("requestContentType",
                JsonArrayBuilder.String(RequestContentType)),
            ("opaqueContinuationStateVersion",
                OpaqueContinuationStateVersion is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(
                        OpaqueContinuationStateVersion)),
            ("semanticDigest", JsonArrayBuilder.String(SemanticDigest)));
    }

    public static ProviderDialectContract Restore(JsonElement evidence)
    {
        try
        {
            _ = JsonValueInspector.ValidateAndMeasure(
                evidence,
                new JsonValueLimits(
                    maxUtf8Bytes: 8_192,
                    maxDepth: 4,
                    maxNodes: 64,
                    maxStringUtf8Bytes: 512,
                    maxContainerItems: 16),
                nameof(evidence));
            if (evidence.ValueKind != JsonValueKind.Object
                || evidence.EnumerateObject().Count() != 12
                || !TryString(
                    evidence,
                    "contractVersion",
                    out var contractVersion)
                || !TryString(evidence, "identifier", out var identifier)
                || !TryInt32(
                    evidence,
                    "requestFamily",
                    out var requestFamily)
                || !TryString(
                    evidence,
                    "requestSchemaVersion",
                    out var requestSchemaVersion)
                || !TryInt32(
                    evidence,
                    "streamFraming",
                    out var streamFraming)
                || !TryString(
                    evidence,
                    "streamFramingVersion",
                    out var streamFramingVersion)
                || !TryString(
                    evidence,
                    "toolCallSemanticsVersion",
                    out var toolCallSemanticsVersion)
                || !TryString(
                    evidence,
                    "usageSemanticsVersion",
                    out var usageSemanticsVersion)
                || !TryString(
                    evidence,
                    "reasoningSemanticsVersion",
                    out var reasoningSemanticsVersion)
                || !TryString(
                    evidence,
                    "requestContentType",
                    out var requestContentType)
                || !TryNullableString(
                    evidence,
                    "opaqueContinuationStateVersion",
                    out var opaqueStateVersion)
                || !TryString(
                    evidence,
                    "semanticDigest",
                    out var semanticDigest)
                || !Enum.IsDefined(
                    typeof(ProviderRequestFamily),
                    requestFamily)
                || !Enum.IsDefined(
                    typeof(ProviderStreamFraming),
                    streamFraming)
                || !CanonicalJsonDigest.IsSha256(semanticDigest))
            {
                throw new InvalidDataException(
                    "The provider dialect contract evidence is invalid.");
            }

            var restored = new ProviderDialectContract(
                identifier!,
                (ProviderRequestFamily)requestFamily,
                requestSchemaVersion!,
                (ProviderStreamFraming)streamFraming,
                streamFramingVersion!,
                toolCallSemanticsVersion!,
                usageSemanticsVersion!,
                reasoningSemanticsVersion!,
                requestContentType!,
                opaqueStateVersion,
                contractVersion!);
            if (!string.Equals(
                    restored.SemanticDigest,
                    semanticDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The provider dialect contract digest is invalid.");
            }

            return restored;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                "The provider dialect contract evidence is invalid.",
                exception);
        }
    }

    internal ProviderDialectContract Snapshot()
    {
        return new ProviderDialectContract(
            Identifier,
            RequestFamily,
            RequestSchemaVersion,
            StreamFraming,
            StreamFramingVersion,
            ToolCallSemanticsVersion,
            UsageSemanticsVersion,
            ReasoningSemanticsVersion,
            RequestContentType,
            OpaqueContinuationStateVersion,
            ContractVersion);
    }

    private static bool TryString(
        JsonElement source,
        string propertyName,
        out string? value)
    {
        value = null;
        return source.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(
                   value = property.GetString());
    }

    private static bool TryNullableString(
        JsonElement source,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!source.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(
                   value = property.GetString());
    }

    private static bool TryInt32(
        JsonElement source,
        string propertyName,
        out int value)
    {
        value = 0;
        return source.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }
}

public static class ProviderWireEvidenceAvailability
{
    public const string Available = "available";

    public const string Unavailable = "unavailable";
}

/// <summary>
/// Non-content evidence for the exact request bytes owned by a prepared
/// provider stream.
/// </summary>
public sealed class ProviderWireRequestEvidence
{
    public const string JournalExtensionName =
        "providerWireRequestEvidence";

    public const string DialectSemanticDigestJournalExtensionName =
        "providerDialectSemanticDigest";

    public const string IntegrityDigestJournalExtensionName =
        "providerWireRequestEvidenceDigest";

    public const string EvidenceVersion =
        "provider-wire-request-evidence.v1";

    public const int MaximumPayloadBytes = 16 * 1_048_576;

    private ProviderWireRequestEvidence(
        string availability,
        ProviderRouteIdentity routeIdentity,
        string? payloadSha256,
        int? payloadByteLength,
        string? contentType,
        string? unavailableReason)
    {
        Availability = availability;
        ProviderId = routeIdentity.ProviderId;
        ProviderRouteDigest = routeIdentity.RouteDigest;
        DialectIdentifier = routeIdentity.TransportDialect;
        DialectSemanticDigest =
            routeIdentity.DialectContract.SemanticDigest;
        PayloadSha256 = payloadSha256;
        PayloadByteLength = payloadByteLength;
        ContentType = contentType;
        UnavailableReason = unavailableReason;
    }

    public string Availability { get; }

    public bool IsAvailable => string.Equals(
        Availability,
        ProviderWireEvidenceAvailability.Available,
        StringComparison.Ordinal);

    public string ProviderId { get; }

    public string ProviderRouteDigest { get; }

    public string DialectIdentifier { get; }

    public string DialectSemanticDigest { get; }

    public string? PayloadSha256 { get; }

    public int? PayloadByteLength { get; }

    public string? ContentType { get; }

    public string? UnavailableReason { get; }

    public static ProviderWireRequestEvidence CreateAvailable(
        ReadOnlySpan<byte> finalPayload,
        string contentType,
        ProviderRouteIdentity routeIdentity)
    {
        if (routeIdentity is null)
        {
            throw new ArgumentNullException(nameof(routeIdentity));
        }

        if (finalPayload.Length < 1
            || finalPayload.Length > MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(finalPayload));
        }

        if (!routeIdentity.HasBoundDialectSemantics)
        {
            throw new ArgumentException(
                "Wire evidence requires a fully specified provider dialect.",
                nameof(routeIdentity));
        }

        var boundedContentType = RuntimeGuard.RequiredUtf8(
            contentType,
            128,
            nameof(contentType));
        if (!string.Equals(
                boundedContentType,
                routeIdentity.DialectContract.RequestContentType,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The content type does not match the provider dialect.",
                nameof(contentType));
        }

        using var sha = SHA256.Create();
        var bytes = finalPayload.ToArray();
        try
        {
            var digest = sha.ComputeHash(bytes);
            return new ProviderWireRequestEvidence(
                ProviderWireEvidenceAvailability.Available,
                routeIdentity,
                ToLowerHex(digest),
                finalPayload.Length,
                boundedContentType,
                unavailableReason: null);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    public static ProviderWireRequestEvidence CreateUnavailable(
        ProviderRouteIdentity routeIdentity,
        string reasonCode = "provider_wire_evidence_unavailable")
    {
        if (routeIdentity is null)
        {
            throw new ArgumentNullException(nameof(routeIdentity));
        }

        return new ProviderWireRequestEvidence(
            ProviderWireEvidenceAvailability.Unavailable,
            routeIdentity,
            payloadSha256: null,
            payloadByteLength: null,
            contentType: null,
            RuntimeGuard.RequiredReasonCode(
                reasonCode,
                nameof(reasonCode)));
    }

    public JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("evidenceVersion", JsonArrayBuilder.String(EvidenceVersion)),
            ("availability", JsonArrayBuilder.String(Availability)),
            ("providerId", JsonArrayBuilder.String(ProviderId)),
            ("providerRouteDigest",
                JsonArrayBuilder.String(ProviderRouteDigest)),
            ("dialectIdentifier",
                JsonArrayBuilder.String(DialectIdentifier)),
            ("dialectSemanticDigest",
                JsonArrayBuilder.String(DialectSemanticDigest)),
            ("payloadSha256",
                PayloadSha256 is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(PayloadSha256)),
            ("payloadByteLength",
                PayloadByteLength.HasValue
                    ? JsonArrayBuilder.Number(PayloadByteLength.Value)
                    : JsonArrayBuilder.Null()),
            ("contentType",
                ContentType is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(ContentType)),
            ("unavailableReason",
                UnavailableReason is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(UnavailableReason)));
    }

    public static ProviderWireRequestEvidence Restore(
        JsonElement evidence,
        ProviderRouteIdentity expectedRouteIdentity)
    {
        if (expectedRouteIdentity is null)
        {
            throw new ArgumentNullException(nameof(expectedRouteIdentity));
        }

        try
        {
            _ = JsonValueInspector.ValidateAndMeasure(
                evidence,
                new JsonValueLimits(
                    maxUtf8Bytes: 4_096,
                    maxDepth: 4,
                    maxNodes: 64,
                    maxStringUtf8Bytes: 512,
                    maxContainerItems: 16),
                nameof(evidence));
            if (evidence.ValueKind != JsonValueKind.Object
                || evidence.EnumerateObject().Count() != 10
                || !TryRequiredString(
                    evidence,
                    "evidenceVersion",
                    out var evidenceVersion)
                || !string.Equals(
                    evidenceVersion,
                    EvidenceVersion,
                    StringComparison.Ordinal)
                || !TryRequiredString(
                    evidence,
                    "availability",
                    out var availability)
                || !TryRequiredString(
                    evidence,
                    "providerId",
                    out var providerId)
                || !TryRequiredString(
                    evidence,
                    "providerRouteDigest",
                    out var routeDigest)
                || !TryRequiredString(
                    evidence,
                    "dialectIdentifier",
                    out var dialectIdentifier)
                || !TryRequiredString(
                    evidence,
                    "dialectSemanticDigest",
                    out var dialectSemanticDigest)
                || !TryNullableString(
                    evidence,
                    "payloadSha256",
                    out var payloadSha256)
                || !TryNullableInt32(
                    evidence,
                    "payloadByteLength",
                    out var payloadByteLength)
                || !TryNullableString(
                    evidence,
                    "contentType",
                    out var contentType)
                || !TryNullableString(
                    evidence,
                    "unavailableReason",
                    out var unavailableReason)
                || !string.Equals(
                    providerId,
                    expectedRouteIdentity.ProviderId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    routeDigest,
                    expectedRouteIdentity.RouteDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    dialectIdentifier,
                    expectedRouteIdentity.TransportDialect,
                    StringComparison.Ordinal)
                || !string.Equals(
                    dialectSemanticDigest,
                    expectedRouteIdentity.DialectSemanticDigest,
                    StringComparison.Ordinal))
            {
                throw InvalidEvidence();
            }

            var restored = new ProviderWireRequestEvidence(
                availability!,
                expectedRouteIdentity,
                payloadSha256,
                payloadByteLength,
                contentType,
                unavailableReason);
            if (!restored.IsAvailable)
            {
                _ = RuntimeGuard.RequiredReasonCode(
                    unavailableReason,
                    nameof(unavailableReason));
            }

            restored.ValidateForRoute(
                expectedRouteIdentity,
                requireAvailable: false);
            return restored;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw InvalidEvidence();
        }
    }

    /// <summary>
    /// Validates persisted wire evidence against the immutable scalar route
    /// identity recorded on the same dispatch event. Recovery deliberately
    /// does not compare historical evidence with the provider's current live
    /// route, which may have legitimately changed since the journal entry.
    /// </summary>
    internal static void ValidateJournalEvidence(
        JsonElement evidence,
        string expectedProviderId,
        string expectedProviderRouteDigest,
        string expectedDialectIdentifier,
        string expectedDialectSemanticDigest,
        string expectedRequestContentType,
        string expectedEvidenceDigest)
    {
        try
        {
            _ = JsonValueInspector.ValidateAndMeasure(
                evidence,
                new JsonValueLimits(
                    maxUtf8Bytes: 4_096,
                    maxDepth: 4,
                    maxNodes: 64,
                    maxStringUtf8Bytes: 512,
                    maxContainerItems: 16),
                nameof(evidence));
            if (evidence.ValueKind != JsonValueKind.Object
                || evidence.EnumerateObject().Count() != 10
                || !TryRequiredString(
                    evidence,
                    "evidenceVersion",
                    out var evidenceVersion)
                || !string.Equals(
                    evidenceVersion,
                    EvidenceVersion,
                    StringComparison.Ordinal)
                || !TryRequiredString(
                    evidence,
                    "availability",
                    out var availability)
                || !TryRequiredString(
                    evidence,
                    "providerId",
                    out var providerId)
                || !TryRequiredString(
                    evidence,
                    "providerRouteDigest",
                    out var routeDigest)
                || !TryRequiredString(
                    evidence,
                    "dialectIdentifier",
                    out var dialectIdentifier)
                || !TryRequiredString(
                    evidence,
                    "dialectSemanticDigest",
                    out var dialectSemanticDigest)
                || !TryNullableString(
                    evidence,
                    "payloadSha256",
                    out var payloadSha256)
                || !TryNullableInt32(
                    evidence,
                    "payloadByteLength",
                    out var payloadByteLength)
                || !TryNullableString(
                    evidence,
                    "contentType",
                    out var contentType)
                || !TryNullableString(
                    evidence,
                    "unavailableReason",
                    out var unavailableReason)
                || !string.Equals(
                    providerId,
                    expectedProviderId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    routeDigest,
                    expectedProviderRouteDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    dialectIdentifier,
                    expectedDialectIdentifier,
                    StringComparison.Ordinal)
                || !string.Equals(
                    dialectSemanticDigest,
                    expectedDialectSemanticDigest,
                    StringComparison.Ordinal)
                || !CanonicalJsonDigest.IsSha256(routeDigest)
                || !CanonicalJsonDigest.IsSha256(dialectSemanticDigest)
                || !CanonicalJsonDigest.IsSha256(expectedEvidenceDigest)
                || !string.Equals(
                    CanonicalJsonDigest.ComputeSha256(evidence),
                    expectedEvidenceDigest,
                    StringComparison.Ordinal))
            {
                throw InvalidEvidence();
            }

            var available = string.Equals(
                availability,
                ProviderWireEvidenceAvailability.Available,
                StringComparison.Ordinal);
            var unavailable = string.Equals(
                availability,
                ProviderWireEvidenceAvailability.Unavailable,
                StringComparison.Ordinal);
            if (available
                    && (!CanonicalJsonDigest.IsSha256(payloadSha256)
                        || payloadByteLength is null
                        || payloadByteLength is < 1 or > MaximumPayloadBytes
                        || string.IsNullOrWhiteSpace(contentType)
                        || Encoding.UTF8.GetByteCount(contentType) > 128
                        || !string.Equals(
                            contentType,
                            expectedRequestContentType,
                            StringComparison.Ordinal)
                        || unavailableReason is not null)
                || unavailable
                    && (payloadSha256 is not null
                        || payloadByteLength is not null
                        || contentType is not null
                        || string.IsNullOrWhiteSpace(unavailableReason))
                || !available && !unavailable)
            {
                throw InvalidEvidence();
            }

            if (unavailable)
            {
                _ = RuntimeGuard.RequiredReasonCode(
                    unavailableReason,
                    nameof(unavailableReason));
            }
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw InvalidEvidence();
        }
    }

    internal void ValidateForRoute(
        ProviderRouteIdentity routeIdentity,
        bool requireAvailable)
    {
        if (routeIdentity is null
            || (!string.Equals(
                    Availability,
                    ProviderWireEvidenceAvailability.Available,
                    StringComparison.Ordinal)
                && !string.Equals(
                    Availability,
                    ProviderWireEvidenceAvailability.Unavailable,
                    StringComparison.Ordinal))
            || !string.Equals(
                ProviderId,
                routeIdentity.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                ProviderRouteDigest,
                routeIdentity.RouteDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                DialectIdentifier,
                routeIdentity.TransportDialect,
                StringComparison.Ordinal)
            || !string.Equals(
                DialectSemanticDigest,
                routeIdentity.DialectContract.SemanticDigest,
                StringComparison.Ordinal)
            || requireAvailable && !IsAvailable
            || IsAvailable
                && (!routeIdentity.HasBoundDialectSemantics
                    || !CanonicalJsonDigest.IsSha256(PayloadSha256)
                    || PayloadByteLength is null
                    || PayloadByteLength is < 1 or > MaximumPayloadBytes
                    || string.IsNullOrWhiteSpace(ContentType)
                    || !string.Equals(
                        ContentType,
                        routeIdentity.DialectContract.RequestContentType,
                        StringComparison.Ordinal)
                    || UnavailableReason is not null)
            || !IsAvailable
                && (PayloadSha256 is not null
                    || PayloadByteLength is not null
                    || ContentType is not null
                    || string.IsNullOrWhiteSpace(UnavailableReason)))
        {
            throw new ProviderException(
                "provider_wire_evidence_invalid",
                "provider",
                "The provider returned invalid wire-request evidence.",
                false,
                usageKnownToBeZero: true);
        }
    }

    private static string ToLowerHex(byte[] digest)
    {
        var result = new StringBuilder(digest.Length * 2);
        foreach (var item in digest)
        {
            result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static bool TryRequiredString(
        JsonElement source,
        string propertyName,
        out string? value)
    {
        value = null;
        return source.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(
                   value = property.GetString());
    }

    private static bool TryNullableString(
        JsonElement source,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!source.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String
               && (value = property.GetString()) is not null;
    }

    private static bool TryNullableInt32(
        JsonElement source,
        string propertyName,
        out int? value)
    {
        value = null;
        if (!source.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static ProviderException InvalidEvidence()
    {
        return new ProviderException(
            "provider_wire_evidence_invalid",
            "provider",
            "The provider wire-request evidence is invalid.",
            false,
            usageKnownToBeZero: true);
    }
}

public sealed class ProviderStreamPreparationContext
{
    public ProviderStreamPreparationContext(
        string providerId,
        ProviderRouteIdentity routeIdentity,
        StreamingModelRequest request)
    {
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        RouteIdentity =
            routeIdentity ?? throw new ArgumentNullException(nameof(routeIdentity));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        if (!string.Equals(
                ProviderId,
                RouteIdentity.ProviderId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The provider id does not match the route identity.",
                nameof(providerId));
        }
    }

    public string ProviderId { get; }

    public ProviderRouteIdentity RouteIdentity { get; }

    public StreamingModelRequest Request { get; }
}

public interface IPreparedStreamingModelProvider
{
    /// <summary>
    /// Produces a one-shot stream that owns its already encoded final request
    /// bytes. Preparation must not perform provider dispatch or acquire a
    /// bearer credential.
    /// </summary>
    ValueTask<PreparedProviderStream> PrepareStreamAsync(
        ProviderStreamPreparationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns the final request representation until its single stream attempt and
/// cleanup finish. Implementations must clear content-bearing buffers during
/// DisposeAsync.
/// </summary>
public abstract class PreparedProviderStream : IAsyncDisposable
{
    protected PreparedProviderStream(ProviderWireRequestEvidence evidence)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public ProviderWireRequestEvidence Evidence { get; }

    public abstract IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();
}

public enum ProviderOpaqueStatePersistence
{
    Ephemeral = 0,
    DurableNonSecret = 1
}

public sealed class ProviderOpaqueContinuationStateException : Exception
{
    internal ProviderOpaqueContinuationStateException(
        string code,
        string safeMessage)
        : base(RuntimeGuard.RequiredUtf8(
            safeMessage,
            512,
            nameof(safeMessage)))
    {
        Code = RuntimeGuard.RequiredReasonCode(code, nameof(code));
    }

    public string Code { get; }
}

public sealed class ProviderOpaqueContinuationUpdate
{
    public ProviderOpaqueContinuationUpdate(
        string stateVersion,
        JsonElement payload,
        ProviderOpaqueStatePersistence persistence =
            ProviderOpaqueStatePersistence.Ephemeral)
    {
        StateVersion = RuntimeGuard.RequiredUtf8(
            stateVersion,
            128,
            nameof(stateVersion));
        if (!Enum.IsDefined(
                typeof(ProviderOpaqueStatePersistence),
                persistence))
        {
            throw new ArgumentOutOfRangeException(nameof(persistence));
        }

        ProviderOpaqueContinuationState.ValidatePayload(payload);
        Payload = payload.Clone();
        Persistence = persistence;
    }

    public string StateVersion { get; }

    public JsonElement Payload { get; }

    public ProviderOpaqueStatePersistence Persistence { get; }
}

/// <summary>
/// A bounded provider-private continuation envelope. Ephemeral is the default;
/// durable storage requires an explicit non-secret declaration.
/// </summary>
public sealed class ProviderOpaqueContinuationState
{
    public const string JournalExtensionName =
        "providerOpaqueContinuationState";

    public const string EnvelopeVersion =
        "provider-opaque-continuation-state.v1";

    public const int MaximumPayloadUtf8Bytes = 65_536;

    private static readonly JsonValueLimits PayloadLimits = new(
        MaximumPayloadUtf8Bytes,
        maxDepth: 32,
        maxNodes: 8_192,
        maxStringUtf8Bytes: MaximumPayloadUtf8Bytes,
        maxContainerItems: 2_048);

    private static readonly JsonValueLimits EnvelopeLimits = new(
        MaximumPayloadUtf8Bytes + 4_096,
        maxDepth: 34,
        maxNodes: 8_208,
        maxStringUtf8Bytes: MaximumPayloadUtf8Bytes,
        maxContainerItems: 2_048);

    private ProviderOpaqueContinuationState(
        string providerId,
        string providerRouteDigest,
        string stateVersion,
        JsonElement payload,
        ProviderOpaqueStatePersistence persistence)
    {
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        if (!CanonicalJsonDigest.IsSha256(providerRouteDigest))
        {
            throw Invalid(
                "provider_opaque_state_route_invalid",
                "The provider continuation route binding is invalid.");
        }

        ProviderRouteDigest = providerRouteDigest;
        StateVersion = RuntimeGuard.RequiredUtf8(
            stateVersion,
            128,
            nameof(stateVersion));
        if (!Enum.IsDefined(
                typeof(ProviderOpaqueStatePersistence),
                persistence))
        {
            throw Invalid(
                "provider_opaque_state_persistence_invalid",
                "The provider continuation persistence declaration is invalid.");
        }

        ValidatePayload(payload);
        Payload = payload.Clone();
        PayloadDigest = CanonicalJsonDigest.ComputeSha256(Payload);
        Persistence = persistence;
    }

    public string ProviderId { get; }

    public string ProviderRouteDigest { get; }

    public string StateVersion { get; }

    public JsonElement Payload { get; }

    public string PayloadDigest { get; }

    public ProviderOpaqueStatePersistence Persistence { get; }

    public bool IsDurableNonSecret =>
        Persistence == ProviderOpaqueStatePersistence.DurableNonSecret;

    public static ProviderOpaqueContinuationState Bind(
        ProviderRouteIdentity routeIdentity,
        ProviderOpaqueContinuationUpdate update)
    {
        if (routeIdentity is null)
        {
            throw new ArgumentNullException(nameof(routeIdentity));
        }

        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        var supportedVersion =
            routeIdentity.DialectContract.OpaqueContinuationStateVersion;
        if (supportedVersion is null
            || !string.Equals(
                supportedVersion,
                update.StateVersion,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "provider_opaque_state_version_unsupported",
                "The provider route does not support this continuation-state version.");
        }

        return new ProviderOpaqueContinuationState(
            routeIdentity.ProviderId,
            routeIdentity.RouteDigest,
            update.StateVersion,
            update.Payload,
            update.Persistence);
    }

    public ProviderOpaqueContinuationState Snapshot()
    {
        return new ProviderOpaqueContinuationState(
            ProviderId,
            ProviderRouteDigest,
            StateVersion,
            Payload,
            Persistence);
    }

    public bool Matches(ProviderRouteIdentity routeIdentity)
    {
        return routeIdentity is not null
               && routeIdentity.DialectContract.SupportsOpaqueContinuationState
               && string.Equals(
                   ProviderId,
                   routeIdentity.ProviderId,
                   StringComparison.Ordinal)
               && string.Equals(
                   ProviderRouteDigest,
                   routeIdentity.RouteDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   StateVersion,
                   routeIdentity.DialectContract
                       .OpaqueContinuationStateVersion,
                   StringComparison.Ordinal)
               && string.Equals(
                   PayloadDigest,
                   CanonicalJsonDigest.ComputeSha256(Payload),
                   StringComparison.Ordinal);
    }

    public bool TryCreateDurableEnvelope(out JsonElement envelope)
    {
        if (!IsDurableNonSecret)
        {
            envelope = default;
            return false;
        }

        envelope = JsonArrayBuilder.Object(
            ("envelopeVersion", JsonArrayBuilder.String(EnvelopeVersion)),
            ("providerId", JsonArrayBuilder.String(ProviderId)),
            ("providerRouteDigest",
                JsonArrayBuilder.String(ProviderRouteDigest)),
            ("stateVersion", JsonArrayBuilder.String(StateVersion)),
            ("persistence",
                JsonArrayBuilder.String("durable_non_secret")),
            ("payloadDigest", JsonArrayBuilder.String(PayloadDigest)),
            ("payload", Payload.Clone()));
        return true;
    }

    public static ProviderOpaqueContinuationState RestoreDurable(
        JsonElement envelope,
        string expectedProviderId,
        string expectedProviderRouteDigest,
        string expectedStateVersion)
    {
        try
        {
            _ = JsonValueInspector.ValidateAndMeasure(
                envelope,
                EnvelopeLimits,
                nameof(envelope));
            if (envelope.ValueKind != JsonValueKind.Object
                || envelope.EnumerateObject().Count() != 7
                || !TryString(
                    envelope,
                    "envelopeVersion",
                    out var envelopeVersion)
                || !string.Equals(
                    envelopeVersion,
                    EnvelopeVersion,
                    StringComparison.Ordinal)
                || !TryString(envelope, "providerId", out var providerId)
                || !TryString(
                    envelope,
                    "providerRouteDigest",
                    out var routeDigest)
                || !TryString(
                    envelope,
                    "stateVersion",
                    out var stateVersion)
                || !TryString(
                    envelope,
                    "persistence",
                    out var persistence)
                || !string.Equals(
                    persistence,
                    "durable_non_secret",
                    StringComparison.Ordinal)
                || !TryString(
                    envelope,
                    "payloadDigest",
                    out var payloadDigest)
                || !envelope.TryGetProperty("payload", out var payload)
                || !string.Equals(
                    providerId,
                    expectedProviderId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    routeDigest,
                    expectedProviderRouteDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    stateVersion,
                    expectedStateVersion,
                    StringComparison.Ordinal)
                || !CanonicalJsonDigest.IsSha256(payloadDigest))
            {
                throw Invalid(
                    "provider_opaque_state_envelope_invalid",
                    "The durable provider continuation envelope is invalid.");
            }

            ValidatePayload(payload);
            if (!string.Equals(
                    CanonicalJsonDigest.ComputeSha256(payload),
                    payloadDigest,
                    StringComparison.Ordinal))
            {
                throw Invalid(
                    "provider_opaque_state_digest_mismatch",
                    "The durable provider continuation envelope failed integrity validation.");
            }

            return new ProviderOpaqueContinuationState(
                providerId!,
                routeDigest!,
                stateVersion!,
                payload,
                ProviderOpaqueStatePersistence.DurableNonSecret);
        }
        catch (ProviderOpaqueContinuationStateException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw Invalid(
                "provider_opaque_state_envelope_invalid",
                "The durable provider continuation envelope is invalid.");
        }
    }

    internal static ProviderOpaqueContinuationState RestoreDurableFromJournal(
        JsonElement envelope,
        string expectedProviderId,
        string expectedProviderRouteDigest)
    {
        if (envelope.ValueKind != JsonValueKind.Object
            || !TryString(envelope, "stateVersion", out var stateVersion))
        {
            throw Invalid(
                "provider_opaque_state_envelope_invalid",
                "The durable provider continuation envelope is invalid.");
        }

        return RestoreDurable(
            envelope,
            expectedProviderId,
            expectedProviderRouteDigest,
            stateVersion!);
    }

    internal static void ValidatePayload(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined)
        {
            throw Invalid(
                "provider_opaque_state_payload_invalid",
                "The provider continuation payload is invalid.");
        }

        int bytes;
        try
        {
            bytes = JsonValueInspector.ValidateAndMeasure(
                payload,
                PayloadLimits,
                nameof(payload));
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw Invalid(
                exception is RuntimeContentLimitException
                {
                    LimitCode: "json_bytes_exceeded"
                }
                    ? "provider_opaque_state_capacity_exceeded"
                    : "provider_opaque_state_payload_invalid",
                "The provider continuation payload is invalid.");
        }

        if (bytes < 1 || bytes > MaximumPayloadUtf8Bytes)
        {
            throw Invalid(
                "provider_opaque_state_capacity_exceeded",
                "The provider continuation payload exceeds its capacity.");
        }

        try
        {
            _ = CanonicalJsonDigest.ComputeSha256(payload);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw Invalid(
                "provider_opaque_state_payload_invalid",
                "The provider continuation payload is invalid.");
        }
    }

    private static bool TryString(
        JsonElement source,
        string propertyName,
        out string? value)
    {
        value = null;
        return source.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
               && (value = property.GetString()) is not null;
    }

    private static ProviderOpaqueContinuationStateException Invalid(
        string code,
        string message)
    {
        return new ProviderOpaqueContinuationStateException(code, message);
    }
}
