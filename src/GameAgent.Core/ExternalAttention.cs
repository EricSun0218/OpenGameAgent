using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Core;

public static class ExternalAttentionStates
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";

    internal static bool IsKnown(string value) =>
        value == Pending || value == Resolved || value == Cancelled || value == Expired;
}

public sealed class ExternalAttentionRequest
{
    public string RequestId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string WorldId { get; set; } = string.Empty;

    public string? RunId { get; set; }

    public string? WorkflowId { get; set; }

    public string? ActorId { get; set; }

    public string AuthorityId { get; set; } = string.Empty;

    public string StateBindingDigest { get; set; } = string.Empty;

    public JsonElement Payload { get; set; }

    public GameTimePoint CreatedAt { get; set; } = null!;

    public GameTimePoint? ExpiresAt { get; set; }
}

public sealed class ExternalAttentionResolution
{
    public string ResolutionId { get; set; } = string.Empty;

    public string AuthorityId { get; set; } = string.Empty;

    public string StateBindingDigest { get; set; } = string.Empty;

    public JsonElement Payload { get; set; }

    public GameTimePoint ResolvedAt { get; set; } = null!;
}

public sealed class ExternalAttentionRecord
{
    public ExternalAttentionRequest Request { get; set; } = new();

    public string RequestDigest { get; set; } = string.Empty;

    public string State { get; set; } = ExternalAttentionStates.Pending;

    public ExternalAttentionResolution? Resolution { get; set; }

    public string? ResolutionDigest { get; set; }

    public long Revision { get; set; }
}

public interface IExternalAttentionStore
{
    ValueTask<ExternalAttentionRecord?> TryGetAsync(
        string requestId,
        CancellationToken cancellationToken);

    ValueTask PutAsync(
        ExternalAttentionRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ExternalAttentionRecord>> ListPendingAsync(
        string? worldId,
        int maximumCount,
        CancellationToken cancellationToken);
}

public sealed class ExternalAttentionException : Exception
{
    public ExternalAttentionException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Persists a typed request that may outlive the current process. It owns no
/// scheduler, engine task, or concurrency slot; the host decides how and when
/// to collect a resolution and resume the associated run or workflow.
/// </summary>
public sealed class ExternalAttentionCoordinator
{
    private readonly IExternalAttentionStore _store;
    private readonly JsonValueLimits _payloadLimits;

    public ExternalAttentionCoordinator(
        IExternalAttentionStore store,
        int maxPayloadUtf8Bytes = 262_144)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (maxPayloadUtf8Bytes is < 1_024
            or > CanonicalJsonDigest.MaximumUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadUtf8Bytes));
        }

        _payloadLimits = new JsonValueLimits(
            maxPayloadUtf8Bytes,
            maxDepth: 64,
            maxNodes: 65_536,
            maxStringUtf8Bytes: maxPayloadUtf8Bytes,
            maxContainerItems: 32_768);
    }

    public async ValueTask<ExternalAttentionRecord> RequestAsync(
        ExternalAttentionRequest request,
        CancellationToken cancellationToken = default)
    {
        var snapshot = SnapshotRequest(request);
        var digest = DigestRequest(snapshot);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var existing = await _store
                .TryGetAsync(snapshot.RequestId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestDigest, digest, StringComparison.Ordinal))
                {
                    throw new ExternalAttentionException(
                        "external_attention_request_conflict",
                        "The external-attention request ID is bound to different content.");
                }

                return Snapshot(existing);
            }

            var created = new ExternalAttentionRecord
            {
                Request = snapshot,
                RequestDigest = digest,
                State = ExternalAttentionStates.Pending,
                Revision = 1
            };
            try
            {
                await _store.PutAsync(created, null, cancellationToken)
                    .ConfigureAwait(false);
                return Snapshot(created);
            }
            catch (ExternalAttentionException exception)
                when (exception.ReasonCode == "external_attention_revision_conflict")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw new ExternalAttentionException(
            "external_attention_contention",
            "The external-attention request changed too frequently to commit safely.");
    }

    public async ValueTask<ExternalAttentionRecord> ResolveAsync(
        string requestId,
        ExternalAttentionResolution resolution,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        requestId = Required(requestId, nameof(requestId), 128);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var current = await RequiredRecordAsync(requestId, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = SnapshotResolution(resolution, current.Request);
            var digest = DigestResolution(snapshot);
            if (current.State == ExternalAttentionStates.Resolved)
            {
                if (string.Equals(current.ResolutionDigest, digest, StringComparison.Ordinal))
                {
                    return Snapshot(current);
                }

                throw new ExternalAttentionException(
                    "external_attention_resolution_conflict",
                    "The request was already resolved with different content.");
            }

            if (current.State != ExternalAttentionStates.Pending)
            {
                throw new ExternalAttentionException(
                    "external_attention_not_pending",
                    "Only a pending external-attention request can be resolved.");
            }

            if (current.Revision != expectedRevision)
            {
                throw new ExternalAttentionException(
                    "external_attention_revision_conflict",
                    "The external-attention request revision changed before resolution.");
            }

            if (current.Request.ExpiresAt is not null
                && current.Request.ExpiresAt.IsComparableTo(snapshot.ResolvedAt)
                && snapshot.ResolvedAt.CompareTo(current.Request.ExpiresAt) >= 0)
            {
                throw new ExternalAttentionException(
                    "external_attention_expired",
                    "The external-attention request expired in game time.");
            }

            current.State = ExternalAttentionStates.Resolved;
            current.Resolution = snapshot;
            current.ResolutionDigest = digest;
            current.Revision++;
            try
            {
                await _store.PutAsync(current, expectedRevision, cancellationToken)
                    .ConfigureAwait(false);
                return Snapshot(current);
            }
            catch (ExternalAttentionException exception)
                when (exception.ReasonCode == "external_attention_revision_conflict")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw new ExternalAttentionException(
            "external_attention_contention",
            "The external-attention request changed too frequently to resolve safely.");
    }

    public async ValueTask<ExternalAttentionRecord> CloseAsync(
        string requestId,
        string terminalState,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (terminalState != ExternalAttentionStates.Cancelled
            && terminalState != ExternalAttentionStates.Expired)
        {
            throw new ArgumentException(
                "External attention can only be cancelled or expired.",
                nameof(terminalState));
        }

        requestId = Required(requestId, nameof(requestId), 128);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var current = await RequiredRecordAsync(requestId, cancellationToken)
                .ConfigureAwait(false);
            if (current.State == terminalState)
            {
                return Snapshot(current);
            }

            if (current.State != ExternalAttentionStates.Pending
                || current.Revision != expectedRevision)
            {
                throw new ExternalAttentionException(
                    "external_attention_revision_conflict",
                    "The external-attention request is no longer pending at that revision.");
            }

            current.State = terminalState;
            current.Revision++;
            try
            {
                await _store.PutAsync(current, expectedRevision, cancellationToken)
                    .ConfigureAwait(false);
                return Snapshot(current);
            }
            catch (ExternalAttentionException exception)
                when (exception.ReasonCode == "external_attention_revision_conflict")
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        throw new ExternalAttentionException(
            "external_attention_contention",
            "The external-attention request changed too frequently to close safely.");
    }

    private async ValueTask<ExternalAttentionRecord> RequiredRecordAsync(
        string requestId,
        CancellationToken cancellationToken) =>
        await _store.TryGetAsync(requestId, cancellationToken).ConfigureAwait(false)
        ?? throw new ExternalAttentionException(
            "external_attention_not_found",
            "The external-attention request does not exist.");

    private ExternalAttentionRequest SnapshotRequest(ExternalAttentionRequest request)
    {
        if (request is null || request.CreatedAt is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.ExpiresAt is not null
            && (!request.CreatedAt.IsComparableTo(request.ExpiresAt)
                || request.CreatedAt.CompareTo(request.ExpiresAt) >= 0))
        {
            throw new ExternalAttentionException(
                "external_attention_time_invalid",
                "The request expiry must be later on the same game-time coordinate.");
        }

        ValidatePayload(request.Payload);
        return new ExternalAttentionRequest
        {
            RequestId = Required(request.RequestId, nameof(request.RequestId), 128),
            Kind = Required(request.Kind, nameof(request.Kind), 128),
            WorldId = Required(request.WorldId, nameof(request.WorldId), 128),
            RunId = Optional(request.RunId, nameof(request.RunId), 128),
            WorkflowId = Optional(request.WorkflowId, nameof(request.WorkflowId), 128),
            ActorId = Optional(request.ActorId, nameof(request.ActorId), 128),
            AuthorityId = Required(request.AuthorityId, nameof(request.AuthorityId), 128),
            StateBindingDigest = Digest(
                request.StateBindingDigest,
                nameof(request.StateBindingDigest)),
            Payload = request.Payload.Clone(),
            CreatedAt = Clone(request.CreatedAt),
            ExpiresAt = request.ExpiresAt is null ? null : Clone(request.ExpiresAt)
        };
    }

    private ExternalAttentionResolution SnapshotResolution(
        ExternalAttentionResolution resolution,
        ExternalAttentionRequest request)
    {
        if (resolution is null || resolution.ResolvedAt is null)
        {
            throw new ArgumentNullException(nameof(resolution));
        }

        ValidatePayload(resolution.Payload);
        var authority = Required(
            resolution.AuthorityId,
            nameof(resolution.AuthorityId),
            128);
        var binding = Digest(
            resolution.StateBindingDigest,
            nameof(resolution.StateBindingDigest));
        if (!string.Equals(authority, request.AuthorityId, StringComparison.Ordinal)
            || !string.Equals(binding, request.StateBindingDigest, StringComparison.Ordinal))
        {
            throw new ExternalAttentionException(
                "external_attention_binding_mismatch",
                "Resolution authority or world-state binding does not match the request.");
        }

        if (!request.CreatedAt.IsComparableTo(resolution.ResolvedAt)
            || resolution.ResolvedAt.CompareTo(request.CreatedAt) < 0)
        {
            throw new ExternalAttentionException(
                "external_attention_time_invalid",
                "Resolution game time is incompatible with the request.");
        }

        return new ExternalAttentionResolution
        {
            ResolutionId = Required(
                resolution.ResolutionId,
                nameof(resolution.ResolutionId),
                128),
            AuthorityId = authority,
            StateBindingDigest = binding,
            Payload = resolution.Payload.Clone(),
            ResolvedAt = Clone(resolution.ResolvedAt)
        };
    }

    private void ValidatePayload(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            throw new ExternalAttentionException(
                "external_attention_payload_invalid",
                "External-attention payload JSON is undefined.");
        }

        JsonValueInspector.ValidateAndMeasure(payload, _payloadLimits, nameof(payload));
    }

    private static string DigestRequest(ExternalAttentionRequest request) =>
        CanonicalJsonDigest.ComputeSha256(JsonArrayBuilder.Object(
            ("requestId", JsonArrayBuilder.String(request.RequestId)),
            ("kind", JsonArrayBuilder.String(request.Kind)),
            ("worldId", JsonArrayBuilder.String(request.WorldId)),
            ("runId", JsonArrayBuilder.String(request.RunId ?? string.Empty)),
            ("workflowId", JsonArrayBuilder.String(request.WorkflowId ?? string.Empty)),
            ("actorId", JsonArrayBuilder.String(request.ActorId ?? string.Empty)),
            ("authorityId", JsonArrayBuilder.String(request.AuthorityId)),
            ("stateBindingDigest", JsonArrayBuilder.String(request.StateBindingDigest)),
            ("payload", request.Payload.Clone()),
            ("createdAt", TimeJson(request.CreatedAt)),
            ("expiresAt", request.ExpiresAt is null
                ? JsonArrayBuilder.Null()
                : TimeJson(request.ExpiresAt))));

    private static string DigestResolution(ExternalAttentionResolution resolution) =>
        CanonicalJsonDigest.ComputeSha256(JsonArrayBuilder.Object(
            ("resolutionId", JsonArrayBuilder.String(resolution.ResolutionId)),
            ("authorityId", JsonArrayBuilder.String(resolution.AuthorityId)),
            ("stateBindingDigest", JsonArrayBuilder.String(resolution.StateBindingDigest)),
            ("payload", resolution.Payload.Clone()),
            ("resolvedAt", TimeJson(resolution.ResolvedAt))));

    private static JsonElement TimeJson(GameTimePoint time) =>
        JsonArrayBuilder.Object(
            ("clockId", JsonArrayBuilder.String(time.ClockId)),
            ("timelineId", JsonArrayBuilder.String(time.TimelineId)),
            ("epoch", JsonArrayBuilder.Number(time.Epoch)),
            ("tick", JsonArrayBuilder.Number(time.Tick)));

    internal static ExternalAttentionRecord Snapshot(ExternalAttentionRecord record) =>
        new()
        {
            Request = new ExternalAttentionRequest
            {
                RequestId = record.Request.RequestId,
                Kind = record.Request.Kind,
                WorldId = record.Request.WorldId,
                RunId = record.Request.RunId,
                WorkflowId = record.Request.WorkflowId,
                ActorId = record.Request.ActorId,
                AuthorityId = record.Request.AuthorityId,
                StateBindingDigest = record.Request.StateBindingDigest,
                Payload = record.Request.Payload.Clone(),
                CreatedAt = Clone(record.Request.CreatedAt),
                ExpiresAt = record.Request.ExpiresAt is null
                    ? null
                    : Clone(record.Request.ExpiresAt)
            },
            RequestDigest = record.RequestDigest,
            State = record.State,
            Resolution = record.Resolution is null
                ? null
                : new ExternalAttentionResolution
                {
                    ResolutionId = record.Resolution.ResolutionId,
                    AuthorityId = record.Resolution.AuthorityId,
                    StateBindingDigest = record.Resolution.StateBindingDigest,
                    Payload = record.Resolution.Payload.Clone(),
                    ResolvedAt = Clone(record.Resolution.ResolvedAt)
                },
            ResolutionDigest = record.ResolutionDigest,
            Revision = record.Revision
        };

    internal static GameTimePoint Clone(GameTimePoint value) =>
        new(value.ClockId, value.TimelineId, value.Epoch, value.Tick);

    private static string Required(string value, string name, int maximum) =>
        RuntimeGuard.RequiredUtf8(value, maximum, name);

    private static string? Optional(string? value, string name, int maximum) =>
        value is null ? null : Required(value, name, maximum);

    private static string Digest(string value, string name)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException("A lowercase SHA-256 digest is required.", name);
        }

        return value;
    }
}

public sealed class InMemoryExternalAttentionStore : IExternalAttentionStore
{
    private readonly int _maximumRecords;
    private readonly ConcurrentDictionary<string, ExternalAttentionRecord> _records =
        new(StringComparer.Ordinal);

    public InMemoryExternalAttentionStore(int maximumRecords = 65_536)
    {
        if (maximumRecords is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        _maximumRecords = maximumRecords;
    }

    public ValueTask<ExternalAttentionRecord?> TryGetAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records.TryGetValue(requestId, out var record);
        return new ValueTask<ExternalAttentionRecord?>(
            record is null ? null : ExternalAttentionCoordinator.Snapshot(record));
    }

    public ValueTask PutAsync(
        ExternalAttentionRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ExternalAttentionCoordinator.Snapshot(record);
        while (true)
        {
            if (_records.TryGetValue(snapshot.Request.RequestId, out var current))
            {
                if (current.Revision != expectedRevision)
                {
                    throw new ExternalAttentionException(
                        "external_attention_revision_conflict",
                        "The external-attention record revision changed.");
                }

                if (_records.TryUpdate(snapshot.Request.RequestId, snapshot, current))
                {
                    return default;
                }

                continue;
            }

            if (expectedRevision is not null)
            {
                throw new ExternalAttentionException(
                    "external_attention_revision_conflict",
                    "The expected external-attention record is missing.");
            }

            if (_records.Count >= _maximumRecords)
            {
                throw new ExternalAttentionException(
                    "external_attention_capacity",
                    "The external-attention store is full.");
            }

            if (_records.TryAdd(snapshot.Request.RequestId, snapshot))
            {
                return default;
            }
        }
    }

    public ValueTask<IReadOnlyList<ExternalAttentionRecord>> ListPendingAsync(
        string? worldId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        IReadOnlyList<ExternalAttentionRecord> result =
            new ReadOnlyCollection<ExternalAttentionRecord>(
                _records.Values
                    .Where(record =>
                        record.State == ExternalAttentionStates.Pending
                        && (worldId is null
                            || record.Request.WorldId == worldId))
                    .OrderBy(record => record.Request.CreatedAt.Epoch)
                    .ThenBy(record => record.Request.CreatedAt.Tick)
                    .ThenBy(record => record.Request.RequestId, StringComparer.Ordinal)
                    .Take(maximumCount)
                    .Select(ExternalAttentionCoordinator.Snapshot)
                    .ToArray());
        return new ValueTask<IReadOnlyList<ExternalAttentionRecord>>(result);
    }
}

public sealed class ScopedCapabilityEvidence
{
    public string CapabilityId { get; set; } = string.Empty;

    public string OperationKind { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public string AuthorityId { get; set; } = string.Empty;

    public string WorldId { get; set; } = string.Empty;

    public string? ActorId { get; set; }

    public string StateBindingDigest { get; set; } = string.Empty;

    public GameTimePoint? ExpiresAt { get; set; }
}

public static class ScopedCapabilityEvidenceValidator
{
    public static ScopedCapabilityEvidence Snapshot(ScopedCapabilityEvidence evidence)
    {
        if (evidence is null)
        {
            throw new ArgumentNullException(nameof(evidence));
        }

        if (!CanonicalJsonDigest.IsSha256(evidence.StateBindingDigest))
        {
            throw new ArgumentException(
                "Capability evidence requires a lowercase SHA-256 state binding.",
                nameof(evidence));
        }

        return new ScopedCapabilityEvidence
        {
            CapabilityId = RuntimeGuard.RequiredUtf8(
                evidence.CapabilityId, 128, nameof(evidence.CapabilityId)),
            OperationKind = RuntimeGuard.RequiredUtf8(
                evidence.OperationKind, 128, nameof(evidence.OperationKind)),
            Target = RuntimeGuard.RequiredUtf8(
                evidence.Target, 512, nameof(evidence.Target)),
            Scope = RuntimeGuard.RequiredUtf8(
                evidence.Scope, 128, nameof(evidence.Scope)),
            AuthorityId = RuntimeGuard.RequiredUtf8(
                evidence.AuthorityId, 128, nameof(evidence.AuthorityId)),
            WorldId = RuntimeGuard.RequiredUtf8(
                evidence.WorldId, 128, nameof(evidence.WorldId)),
            ActorId = evidence.ActorId is null
                ? null
                : RuntimeGuard.RequiredUtf8(
                    evidence.ActorId, 128, nameof(evidence.ActorId)),
            StateBindingDigest = evidence.StateBindingDigest,
            ExpiresAt = evidence.ExpiresAt is null
                ? null
                : ExternalAttentionCoordinator.Clone(evidence.ExpiresAt)
        };
    }

    public static bool Matches(
        ScopedCapabilityEvidence evidence,
        string operationKind,
        string target,
        string worldId,
        string? actorId,
        string stateBindingDigest,
        GameTimePoint now)
    {
        var snapshot = Snapshot(evidence);
        return string.Equals(snapshot.OperationKind, operationKind, StringComparison.Ordinal)
            && string.Equals(snapshot.Target, target, StringComparison.Ordinal)
            && string.Equals(snapshot.WorldId, worldId, StringComparison.Ordinal)
            && string.Equals(snapshot.ActorId, actorId, StringComparison.Ordinal)
            && string.Equals(
                snapshot.StateBindingDigest,
                stateBindingDigest,
                StringComparison.Ordinal)
            && (snapshot.ExpiresAt is null
                || snapshot.ExpiresAt.IsComparableTo(now)
                && now.CompareTo(snapshot.ExpiresAt) < 0);
    }
}
