using System.Collections.ObjectModel;
using System.Globalization;

namespace GameAgent.Core;

public static class MemoryBatchLimits
{
    public const int MaxMutations = 1_024;
    public const int MaxAggregateContentUtf8Bytes = 8 * 1024 * 1024;
    public const int MaxInMemoryIdempotencyKeys = 100_000;
}

public static class MemoryBatchReasonCodes
{
    public const string Empty = "memory_batch_empty";
    public const string TooManyMutations =
        "memory_batch_mutation_count_exceeded";
    public const string NullMutation = "memory_batch_null_mutation";
    public const string DuplicateMemoryId = "memory_batch_duplicate_id";
    public const string AggregateContentBytesExceeded =
        "memory_batch_content_bytes_exceeded";
    public const string NotSupported = "memory_batch_not_supported";
    public const string IdempotencyConflict =
        "memory_batch_idempotency_conflict";
    public const string IdempotencyNotSupported =
        "memory_batch_idempotency_not_supported";
    public const string IdempotencyCapacityExceeded =
        "memory_batch_idempotency_capacity_exceeded";
    public const string RuntimeMutationContractNotSupported =
        "memory_runtime_mutation_contract_not_supported";
    public const string NamespaceConflict =
        "memory_record_namespace_conflict";
    public const string PreconditionFailed =
        "memory_record_precondition_failed";
}

public enum MemoryMutationKind
{
    Upsert = 0,
    Delete = 1
}

/// <summary>
/// Immutable authority boundary carried by a conditional memory mutation.
/// It deliberately excludes evolving values such as save revision from
/// namespace equality while retaining them for runtime authorization.
/// </summary>
public sealed class MemoryRecordAuthorityEnvelope
{
    private MemoryRecordAuthorityEnvelope(
        bool hasProvenance,
        string? worldId,
        string? sessionId,
        long? saveRevision,
        bool committed,
        string? timelineId,
        long? timelineEpoch,
        bool hasPerspective,
        string? observerEntityId,
        long? observerIncarnation,
        string? perspectiveKind,
        bool hasSource,
        string? sourceEntityId,
        long? sourceIncarnation,
        bool hasGameTimeWindow,
        string? gameTimeClockId,
        string? gameTimeTimelineId,
        long? gameTimeEpoch)
    {
        if (hasProvenance != (worldId is not null)
            || hasProvenance != saveRevision.HasValue
            || !hasProvenance
            && (sessionId is not null
                || committed
                || timelineId is not null
                || timelineEpoch.HasValue
                || hasPerspective))
        {
            throw new ArgumentException(
                "Memory authority provenance is inconsistent.",
                nameof(hasProvenance));
        }

        if (saveRevision < 0 || timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        if ((hasPerspective
                ? observerEntityId is null
                  || !observerIncarnation.HasValue
                  || perspectiveKind is null
                : observerEntityId is not null
                  || observerIncarnation.HasValue
                  || perspectiveKind is not null
                  || hasSource)
            || (hasSource
                ? sourceEntityId is null || !sourceIncarnation.HasValue
                : sourceEntityId is not null || sourceIncarnation.HasValue)
            || observerIncarnation < 0
            || sourceIncarnation < 0)
        {
            throw new ArgumentException(
                "Memory authority perspective is inconsistent.",
                nameof(hasPerspective));
        }

        if ((hasGameTimeWindow
                ? gameTimeClockId is null
                  || gameTimeTimelineId is null
                  || !gameTimeEpoch.HasValue
                : gameTimeClockId is not null
                  || gameTimeTimelineId is not null
                  || gameTimeEpoch.HasValue)
            || gameTimeEpoch < 0)
        {
            throw new ArgumentException(
                "Memory authority game-time identity is inconsistent.",
                nameof(hasGameTimeWindow));
        }

        HasProvenance = hasProvenance;
        WorldId = Optional(worldId, 128, nameof(worldId));
        SessionId = Optional(sessionId, 128, nameof(sessionId));
        SaveRevision = saveRevision;
        Committed = committed;
        TimelineId = Optional(timelineId, 128, nameof(timelineId));
        TimelineEpoch = timelineEpoch;
        HasPerspective = hasPerspective;
        ObserverEntityId = Optional(
            observerEntityId,
            128,
            nameof(observerEntityId));
        ObserverIncarnation = observerIncarnation;
        PerspectiveKind = Optional(
            perspectiveKind,
            128,
            nameof(perspectiveKind));
        HasSource = hasSource;
        SourceEntityId = Optional(
            sourceEntityId,
            128,
            nameof(sourceEntityId));
        SourceIncarnation = sourceIncarnation;
        HasGameTimeWindow = hasGameTimeWindow;
        GameTimeClockId = Optional(
            gameTimeClockId,
            128,
            nameof(gameTimeClockId));
        GameTimeTimelineId = Optional(
            gameTimeTimelineId,
            128,
            nameof(gameTimeTimelineId));
        GameTimeEpoch = gameTimeEpoch;
    }

    public bool HasProvenance { get; }

    public string? WorldId { get; }

    public string? SessionId { get; }

    public long? SaveRevision { get; }

    public bool Committed { get; }

    public string? TimelineId { get; }

    public long? TimelineEpoch { get; }

    public bool HasPerspective { get; }

    public string? ObserverEntityId { get; }

    public long? ObserverIncarnation { get; }

    public string? PerspectiveKind { get; }

    public bool HasSource { get; }

    public string? SourceEntityId { get; }

    public long? SourceIncarnation { get; }

    public bool HasGameTimeWindow { get; }

    public string? GameTimeClockId { get; }

    public string? GameTimeTimelineId { get; }

    public long? GameTimeEpoch { get; }

    internal static MemoryRecordAuthorityEnvelope FromRecord(
        MemoryRecord record)
    {
        var provenance = record.Provenance;
        var perspective = provenance?.Perspective;
        var source = perspective?.Source;
        var time = record.GameTimeWindow?.ValidFrom
                   ?? record.GameTimeWindow?.ValidUntil;
        return new MemoryRecordAuthorityEnvelope(
            provenance is not null,
            provenance?.WorldId,
            provenance?.SessionId,
            provenance?.SaveRevision,
            provenance?.Committed ?? false,
            provenance?.TimelineId,
            provenance?.TimelineEpoch,
            perspective is not null,
            perspective?.Observer.EntityId,
            perspective?.Observer.Incarnation,
            perspective?.KnowledgeKind,
            source is not null,
            source?.EntityId,
            source?.Incarnation,
            time is not null,
            time?.ClockId,
            time?.TimelineId,
            time?.Epoch);
    }

    internal static MemoryRecordAuthorityEnvelope Restore(
        bool hasProvenance,
        string? worldId,
        string? sessionId,
        long? saveRevision,
        bool committed,
        string? timelineId,
        long? timelineEpoch,
        bool hasPerspective,
        string? observerEntityId,
        long? observerIncarnation,
        string? perspectiveKind,
        bool hasSource,
        string? sourceEntityId,
        long? sourceIncarnation,
        bool hasGameTimeWindow,
        string? gameTimeClockId,
        string? gameTimeTimelineId,
        long? gameTimeEpoch)
    {
        return new MemoryRecordAuthorityEnvelope(
            hasProvenance,
            worldId,
            sessionId,
            saveRevision,
            committed,
            timelineId,
            timelineEpoch,
            hasPerspective,
            observerEntityId,
            observerIncarnation,
            perspectiveKind,
            hasSource,
            sourceEntityId,
            sourceIncarnation,
            hasGameTimeWindow,
            gameTimeClockId,
            gameTimeTimelineId,
            gameTimeEpoch);
    }

    internal bool IsSameNamespace(MemoryRecordAuthorityEnvelope other)
    {
        return other is not null
               && HasProvenance == other.HasProvenance
               && string.Equals(WorldId, other.WorldId, StringComparison.Ordinal)
               && string.Equals(SessionId, other.SessionId, StringComparison.Ordinal)
               && string.Equals(TimelineId, other.TimelineId, StringComparison.Ordinal)
               && TimelineEpoch == other.TimelineEpoch
               && HasPerspective == other.HasPerspective
               && string.Equals(
                   ObserverEntityId,
                   other.ObserverEntityId,
                   StringComparison.Ordinal)
               && ObserverIncarnation == other.ObserverIncarnation
               && string.Equals(
                   PerspectiveKind,
                   other.PerspectiveKind,
                   StringComparison.Ordinal)
               && HasSource == other.HasSource
               && string.Equals(
                   SourceEntityId,
                   other.SourceEntityId,
                   StringComparison.Ordinal)
               && SourceIncarnation == other.SourceIncarnation
               && HasGameTimeWindow == other.HasGameTimeWindow
               && string.Equals(
                   GameTimeClockId,
                   other.GameTimeClockId,
                   StringComparison.Ordinal)
               && string.Equals(
                   GameTimeTimelineId,
                   other.GameTimeTimelineId,
                   StringComparison.Ordinal)
               && GameTimeEpoch == other.GameTimeEpoch;
    }

    private static string? Optional(string? value, int maxBytes, string name)
    {
        return value is null
            ? null
            : RuntimeGuard.RequiredUtf8(value, maxBytes, name);
    }
}

/// <summary>
/// Immutable identity and digest of the record that a conditional mutation
/// expects to find. Create it from an actual record so a delete or replacement
/// cannot be redirected to another game-semantic authority that reused the ID.
/// </summary>
public sealed class MemoryRecordExpectation
{
    private MemoryRecordExpectation(
        string memoryId,
        string scope,
        MemoryRecordAuthorityEnvelope authority,
        string recordDigest)
    {
        MemoryId = RuntimeGuard.RequiredUtf8(
            memoryId,
            128,
            nameof(memoryId));
        Scope = RuntimeGuard.RequiredUtf8(scope, 256, nameof(scope));
        Authority = authority
                    ?? throw new ArgumentNullException(nameof(authority));
        if (!CanonicalJsonDigest.IsSha256(recordDigest))
        {
            throw new ArgumentException(
                "A memory record expectation requires a SHA-256 digest.",
                nameof(recordDigest));
        }

        RecordDigest = recordDigest;
    }

    public string MemoryId { get; }

    public string Scope { get; }

    public MemoryRecordAuthorityEnvelope Authority { get; }

    public bool HasProvenance => Authority.HasProvenance;

    public string? WorldId => Authority.WorldId;

    public string? SessionId => Authority.SessionId;

    public string RecordDigest { get; }

    public static MemoryRecordExpectation FromRecord(MemoryRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new MemoryRecordExpectation(
            record.MemoryId,
            record.Scope,
            MemoryRecordAuthorityEnvelope.FromRecord(record),
            MemoryRecordDigest.ComputeSha256(record));
    }

    internal static MemoryRecordExpectation Restore(
        string memoryId,
        string scope,
        MemoryRecordAuthorityEnvelope authority,
        string recordDigest)
    {
        return new MemoryRecordExpectation(
            memoryId,
            scope,
            authority,
            recordDigest);
    }
}

public static class MemoryRecordDigest
{
    public static string ComputeSha256(MemoryRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "memory-record-v1");
        digest.Add("memoryId", record.MemoryId);
        digest.Add("scope", record.Scope);
        digest.Add("content", record.Content);
        digest.Add("tags", record.Tags);
        digest.Add("importance", record.Importance);
        digest.Add("createdAt", Timestamp(record.CreatedAt));
        digest.Add("updatedAt", Timestamp(record.UpdatedAt));
        digest.Add(
            "expiresAt",
            record.ExpiresAt.HasValue
                ? Timestamp(record.ExpiresAt.Value)
                : null);

        var provenance = record.Provenance;
        digest.Add("provenance.present", provenance is null ? "false" : "true");
        if (provenance is not null)
        {
            digest.Add("provenance.worldId", provenance.WorldId);
            digest.Add("provenance.sessionId", provenance.SessionId);
            digest.Add("provenance.saveRevision", provenance.SaveRevision);
            digest.Add("provenance.sourceRunId", provenance.SourceRunId);
            digest.Add("provenance.sourceEventId", provenance.SourceEventId);
            digest.Add(
                "provenance.committed",
                provenance.Committed ? "true" : "false");
            digest.Add("provenance.timelineId", provenance.TimelineId);
            digest.Add(
                "provenance.timelineEpoch",
                provenance.TimelineEpoch?.ToString(CultureInfo.InvariantCulture));
            var perspective = provenance.Perspective;
            digest.Add(
                "provenance.perspective.present",
                perspective is null ? "false" : "true");
            if (perspective is not null)
            {
                AddIdentity(digest, "provenance.perspective.observer", perspective.Observer);
                digest.Add(
                    "provenance.perspective.knowledgeKind",
                    perspective.KnowledgeKind);
                AddIdentity(
                    digest,
                    "provenance.perspective.source",
                    perspective.Source);
            }
        }

        var window = record.GameTimeWindow;
        digest.Add("gameTimeWindow.present", window is null ? "false" : "true");
        if (window is not null)
        {
            AddTime(digest, "gameTimeWindow.validFrom", window.ValidFrom);
            AddTime(digest, "gameTimeWindow.validUntil", window.ValidUntil);
        }

        return digest.Finish();
    }

    private static string Timestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static void AddIdentity(
        CanonicalDigestBuilder digest,
        string prefix,
        GameEntityIdentity? identity)
    {
        digest.Add(prefix + ".present", identity is null ? "false" : "true");
        if (identity is null)
        {
            return;
        }

        digest.Add(prefix + ".entityId", identity.EntityId);
        digest.Add(prefix + ".incarnation", identity.Incarnation);
    }

    private static void AddTime(
        CanonicalDigestBuilder digest,
        string prefix,
        GameTimePoint? point)
    {
        digest.Add(prefix + ".present", point is null ? "false" : "true");
        if (point is null)
        {
            return;
        }

        digest.Add(prefix + ".clockId", point.ClockId);
        digest.Add(prefix + ".timelineId", point.TimelineId);
        digest.Add(prefix + ".epoch", point.Epoch);
        digest.Add(prefix + ".tick", point.Tick);
    }
}

public sealed class MemoryMutation
{
    private MemoryMutation(
        MemoryMutationKind kind,
        string memoryId,
        MemoryRecord? record,
        MemoryRecordExpectation? expectedRecord)
    {
        Kind = kind;
        MemoryId = RuntimeGuard.RequiredUtf8(
            memoryId,
            128,
            nameof(memoryId));
        Record = record;
        ExpectedRecord = expectedRecord;
        if (expectedRecord is not null
            && !string.Equals(
                memoryId,
                expectedRecord.MemoryId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A memory mutation expectation targets another memory ID.",
                nameof(expectedRecord));
        }
    }

    public MemoryMutationKind Kind { get; }

    public string MemoryId { get; }

    public MemoryRecord? Record { get; }

    public MemoryRecordExpectation? ExpectedRecord { get; }

    public static MemoryMutation Upsert(MemoryRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new MemoryMutation(
            MemoryMutationKind.Upsert,
            record.MemoryId,
            record,
            expectedRecord: null);
    }

    public static MemoryMutation Upsert(
        MemoryRecord record,
        MemoryRecord expectedRecord)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        return new MemoryMutation(
            MemoryMutationKind.Upsert,
            record.MemoryId,
            record,
            MemoryRecordExpectation.FromRecord(expectedRecord));
    }

    public static MemoryMutation Delete(string memoryId)
    {
        return new MemoryMutation(
            MemoryMutationKind.Delete,
            memoryId,
            record: null,
            expectedRecord: null);
    }

    public static MemoryMutation Delete(MemoryRecord expectedRecord)
    {
        if (expectedRecord is null)
        {
            throw new ArgumentNullException(nameof(expectedRecord));
        }

        return new MemoryMutation(
            MemoryMutationKind.Delete,
            expectedRecord.MemoryId,
            record: null,
            MemoryRecordExpectation.FromRecord(expectedRecord));
    }

    internal static MemoryMutation Restore(
        MemoryMutationKind kind,
        string memoryId,
        MemoryRecord? record,
        MemoryRecordExpectation? expectedRecord)
    {
        return new MemoryMutation(kind, memoryId, record, expectedRecord);
    }
}

public sealed class MemoryMutationConflictException : InvalidOperationException
{
    internal MemoryMutationConflictException(
        string reasonCode,
        string memoryId,
        string message)
        : base(message)
    {
        ReasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
        MemoryId = RuntimeGuard.RequiredUtf8(
            memoryId,
            128,
            nameof(memoryId));
    }

    public string ReasonCode { get; }

    public string MemoryId { get; }
}

/// <summary>
/// Shared compare-and-swap admission for atomic memory stores. Third-party
/// stores implementing the runtime mutation contract must invoke this check
/// against the record observed inside the same atomic transaction that applies
/// the mutation.
/// </summary>
public static class MemoryMutationAdmission
{
    public static void EnsureCanApply(
        MemoryMutation mutation,
        MemoryRecord? existing)
    {
        EnsureCanApplyCore(
            mutation,
            existing,
            allowUnconditionalUpsert: false);
    }

    internal static void EnsureCanApplyUnconditionalUpsert(
        MemoryMutation mutation,
        MemoryRecord? existing)
    {
        EnsureCanApplyCore(
            mutation,
            existing,
            allowUnconditionalUpsert: true);
    }

    internal static void EnsureCanReplayLegacy(MemoryMutation mutation)
    {
        if (mutation is null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }

        if (mutation.ExpectedRecord is not null)
        {
            throw new InvalidDataException(
                "A legacy memory mutation cannot carry a record expectation.");
        }
    }

    private static void EnsureCanApplyCore(
        MemoryMutation mutation,
        MemoryRecord? existing,
        bool allowUnconditionalUpsert)
    {
        if (mutation is null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }

        if (existing is null)
        {
            if (mutation.Kind == MemoryMutationKind.Upsert
                && mutation.ExpectedRecord is not null)
            {
                throw PreconditionFailed(mutation.MemoryId);
            }

            return;
        }

        if (mutation.ExpectedRecord is not null
            && !Matches(mutation.ExpectedRecord, existing))
        {
            throw PreconditionFailed(mutation.MemoryId);
        }

        if (mutation.Kind == MemoryMutationKind.Upsert
            && mutation.ExpectedRecord is null
            && !allowUnconditionalUpsert)
        {
            throw PreconditionFailed(mutation.MemoryId);
        }

        if (mutation.Kind != MemoryMutationKind.Upsert)
        {
            return;
        }

        var incoming = mutation.Record
                       ?? throw new InvalidOperationException(
                           "An upsert mutation requires a record.");
        if (!SameNamespace(existing, incoming))
        {
            throw new MemoryMutationConflictException(
                MemoryBatchReasonCodes.NamespaceConflict,
                mutation.MemoryId,
                "A memory ID already belongs to another game-semantic authority.");
        }
    }

    private static bool Matches(
        MemoryRecordExpectation expectation,
        MemoryRecord existing)
    {
        return string.Equals(
                   expectation.MemoryId,
                   existing.MemoryId,
                   StringComparison.Ordinal)
               && string.Equals(
                   expectation.Scope,
                   existing.Scope,
                   StringComparison.Ordinal)
               && expectation.Authority.IsSameNamespace(
                   MemoryRecordAuthorityEnvelope.FromRecord(existing))
               && string.Equals(
                   expectation.RecordDigest,
                   MemoryRecordDigest.ComputeSha256(existing),
                   StringComparison.Ordinal);
    }

    private static bool SameNamespace(
        MemoryRecord existing,
        MemoryRecord incoming)
    {
        if (!string.Equals(
                existing.Scope,
                incoming.Scope,
                StringComparison.Ordinal))
        {
            return false;
        }

        return MemoryRecordAuthorityEnvelope.FromRecord(existing)
            .IsSameNamespace(
                MemoryRecordAuthorityEnvelope.FromRecord(incoming));
    }

    private static MemoryMutationConflictException PreconditionFailed(
        string memoryId)
    {
        return new MemoryMutationConflictException(
            MemoryBatchReasonCodes.PreconditionFailed,
            memoryId,
            "The current memory record does not match the expected record.");
    }
}

public sealed class MemoryMutationResult
{
    public MemoryMutationResult(
        MemoryMutationKind kind,
        string memoryId,
        bool changed)
    {
        if (kind is not MemoryMutationKind.Upsert
            and not MemoryMutationKind.Delete)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        MemoryId = RuntimeGuard.RequiredUtf8(
            memoryId,
            128,
            nameof(memoryId));
        Changed = changed;
    }

    public MemoryMutationKind Kind { get; }

    public string MemoryId { get; }

    public bool Changed { get; }
}

public sealed class MemoryBatchValidationException : ArgumentException
{
    internal MemoryBatchValidationException(
        string reasonCode,
        string message,
        int? mutationIndex = null,
        string? memoryId = null)
        : base($"{reasonCode}: {message}", "mutations")
    {
        ReasonCode = reasonCode;
        MutationIndex = mutationIndex;
        MemoryId = memoryId;
    }

    public string ReasonCode { get; }

    public int? MutationIndex { get; }

    public string? MemoryId { get; }
}

public sealed class MemoryBatchNotSupportedException : NotSupportedException
{
    public MemoryBatchNotSupportedException()
        : base(
            "The configured memory write store does not support atomic "
            + "batches.")
    {
    }

    public string ReasonCode => MemoryBatchReasonCodes.NotSupported;
}

public interface IAtomicMemoryBatchStore : IMemoryStore
{
    ValueTask<IReadOnlyList<MemoryMutationResult>> ApplyAtomicBatchAsync(
        IReadOnlyList<MemoryMutation> mutations,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Atomic memory store that durably deduplicates batches by commit identity.
/// Reusing an identity with the same payload digest is a no-op; reusing it
/// with a different digest must fail.
/// </summary>
public interface IIdempotentAtomicMemoryBatchStore : IAtomicMemoryBatchStore
{
    ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default);
}

/// <summary>
/// Versioned contract required for runtime-managed memory writeback. A store
/// implementing this capability guarantees that create-only upserts and full
/// authority-aware compare-and-swap expectations are evaluated atomically
/// with the mutation by using <see cref="MemoryMutationAdmission"/>.
/// </summary>
public interface IRuntimeAuthoritativeMemoryBatchStore :
    IIdempotentAtomicMemoryBatchStore
{
    int RuntimeMutationContractVersion { get; }
}

/// <summary>
/// Optional upgrade bridge for replaying a durable runtime-memory commit
/// written before the authority-aware mutation contract existed. The method
/// preserves the historical unconditional upsert/delete semantics and must
/// still deduplicate atomically by <paramref name="commitId"/>. New work must
/// use <see cref="IRuntimeAuthoritativeMemoryBatchStore"/> instead.
/// </summary>
public interface ILegacyRuntimeMemoryBatchReplayStore :
    IIdempotentAtomicMemoryBatchStore
{
    ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyLegacyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default);
}

public static class RuntimeMemoryMutationContract
{
    public const int CurrentVersion = 1;
}

public sealed class MemoryIdempotentBatchNotSupportedException
    : NotSupportedException
{
    public MemoryIdempotentBatchNotSupportedException()
        : base(
            "The configured memory write store does not support durable "
            + "idempotent atomic batches.")
    {
    }

    public string ReasonCode => MemoryBatchReasonCodes.IdempotencyNotSupported;
}

public sealed class MemoryRuntimeMutationContractNotSupportedException
    : NotSupportedException
{
    public MemoryRuntimeMutationContractNotSupportedException(
        int? advertisedVersion = null)
        : base(
            advertisedVersion.HasValue
                ? "The configured memory write store advertises unsupported "
                  + $"runtime mutation contract version "
                  + $"'{advertisedVersion.Value}'."
                : "The configured memory write store does not implement the "
                  + "runtime authority-aware mutation contract.")
    {
        AdvertisedVersion = advertisedVersion;
    }

    public int? AdvertisedVersion { get; }

    public string ReasonCode =>
        MemoryBatchReasonCodes.RuntimeMutationContractNotSupported;
}

public sealed class MemoryLegacyReplayNotSupportedException
    : NotSupportedException
{
    public MemoryLegacyReplayNotSupportedException()
        : base(
            "The configured memory write store cannot replay a durable "
            + "memory commit written before the authority-aware mutation "
            + "contract.")
    {
    }
}

public sealed class MemoryBatchIdempotencyConflictException
    : InvalidOperationException
{
    public MemoryBatchIdempotencyConflictException(string commitId)
        : base(
            "A memory batch commit identity was reused with a different "
            + "payload digest.")
    {
        CommitId = RuntimeGuard.RequiredUtf8(
            commitId,
            256,
            nameof(commitId));
    }

    public string CommitId { get; }

    public string ReasonCode => MemoryBatchReasonCodes.IdempotencyConflict;
}

internal static class MemoryBatchValidator
{
    public static MemoryMutation[] Snapshot(
        IReadOnlyList<MemoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (mutations is null)
        {
            throw new ArgumentNullException(nameof(mutations));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // IReadOnlyList<T>.Count is supplied by the caller and is not a
        // trustworthy resource bound. Enumerate once into an owned snapshot,
        // stopping immediately at the first item beyond the hard cap.
        var snapshot = new List<MemoryMutation>();
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Count >= MemoryBatchLimits.MaxMutations)
            {
                throw new MemoryBatchValidationException(
                    MemoryBatchReasonCodes.TooManyMutations,
                    $"A memory batch exceeds "
                    + $"{MemoryBatchLimits.MaxMutations} mutations.");
            }

            snapshot.Add(mutation);
        }

        if (snapshot.Count == 0)
        {
            throw new MemoryBatchValidationException(
                MemoryBatchReasonCodes.Empty,
                "A memory batch requires at least one mutation.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var aggregateContentBytes = 0L;
        for (var index = 0; index < snapshot.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = snapshot[index];
            if (mutation is null)
            {
                throw new MemoryBatchValidationException(
                    MemoryBatchReasonCodes.NullMutation,
                    $"Memory mutation {index} is null.",
                    index);
            }

            if (!ids.Add(mutation.MemoryId))
            {
                throw new MemoryBatchValidationException(
                    MemoryBatchReasonCodes.DuplicateMemoryId,
                    $"Memory id '{mutation.MemoryId}' appears more than once "
                    + "in the same atomic batch.",
                    index,
                    mutation.MemoryId);
            }

            if (mutation.Kind == MemoryMutationKind.Upsert)
            {
                var record = mutation.Record
                             ?? throw new InvalidOperationException(
                                 "An upsert mutation requires a record.");
                var contentBytes = JsonValueInspector.ValidateAndMeasure(
                    record.Content,
                    new JsonValueLimits(maxUtf8Bytes: 131_072),
                    nameof(mutations));
                aggregateContentBytes += contentBytes;
                if (aggregateContentBytes
                    > MemoryBatchLimits.MaxAggregateContentUtf8Bytes)
                {
                    throw new MemoryBatchValidationException(
                        MemoryBatchReasonCodes.AggregateContentBytesExceeded,
                        $"Memory batch content exceeds "
                        + $"{MemoryBatchLimits.MaxAggregateContentUtf8Bytes} "
                        + "UTF-8 bytes.",
                        index,
                        mutation.MemoryId);
                }
            }
        }

        return snapshot.ToArray();
    }
}

public sealed partial class DeterministicMemoryStore
{
    private readonly Dictionary<string, string> _idempotentBatchDigests =
        new(StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        return ApplyIdempotentAtomicBatchCoreAsync(
            commitId,
            mutations,
            allowLegacyReplay: false,
            cancellationToken);
    }

    public ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyLegacyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        return ApplyIdempotentAtomicBatchCoreAsync(
            commitId,
            mutations,
            allowLegacyReplay: true,
            cancellationToken);
    }

    private ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchCoreAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            bool allowLegacyReplay,
            CancellationToken cancellationToken)
    {
        commitId = RuntimeGuard.RequiredUtf8(
            commitId,
            256,
            nameof(commitId));
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        var payloadDigest =
            RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(snapshot);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_idempotentBatchDigests.TryGetValue(
                    commitId,
                    out var existingDigest))
            {
                if (!string.Equals(
                        existingDigest,
                        payloadDigest,
                        StringComparison.Ordinal))
                {
                    throw new MemoryBatchIdempotencyConflictException(commitId);
                }

                return new ValueTask<IReadOnlyList<MemoryMutationResult>>(
                    new ReadOnlyCollection<MemoryMutationResult>(
                        snapshot
                            .Select(
                                item => new MemoryMutationResult(
                                    item.Kind,
                                    item.MemoryId,
                                    changed: false))
                            .ToArray()));
            }

            if (_idempotentBatchDigests.Count
                >= MemoryBatchLimits.MaxInMemoryIdempotencyKeys)
            {
                throw new RuntimeContentLimitException(
                    nameof(commitId),
                    MemoryBatchReasonCodes.IdempotencyCapacityExceeded,
                    "Memory batch idempotency capacity is exhausted.");
            }

            var result = ApplyAtomicBatchCore(
                snapshot,
                allowLegacyReplay,
                cancellationToken);
            _idempotentBatchDigests.Add(commitId, payloadDigest);
            return new ValueTask<IReadOnlyList<MemoryMutationResult>>(result);
        }
    }

    public ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyAtomicBatchAsync(
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        return new ValueTask<IReadOnlyList<MemoryMutationResult>>(
            ApplyAtomicBatchCore(
                snapshot,
                allowLegacyReplay: false,
                cancellationToken));
    }

    private IReadOnlyList<MemoryMutationResult> ApplyAtomicBatchCore(
        MemoryMutation[] snapshot,
        bool allowLegacyReplay,
        CancellationToken cancellationToken)
    {
        var prepared = new IndexedRecord?[snapshot.Length];
        for (var index = 0; index < snapshot.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = snapshot[index];
            if (mutation.Kind == MemoryMutationKind.Upsert)
            {
                var record = mutation.Record
                             ?? throw new InvalidOperationException(
                                 "An upsert mutation requires a record.");
                prepared[index] = new IndexedRecord(
                    record,
                    Tokenize(record.Content));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staged = new Dictionary<string, IndexedRecord>(
                _records,
                StringComparer.Ordinal);
            var results = new MemoryMutationResult[snapshot.Length];
            for (var index = 0; index < snapshot.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mutation = snapshot[index];
                staged.TryGetValue(mutation.MemoryId, out var existing);
                if (allowLegacyReplay)
                {
                    MemoryMutationAdmission.EnsureCanReplayLegacy(mutation);
                }
                else
                {
                    MemoryMutationAdmission.EnsureCanApply(
                        mutation,
                        existing?.Record);
                }
                switch (mutation.Kind)
                {
                    case MemoryMutationKind.Upsert:
                        staged[mutation.MemoryId] = prepared[index]
                            ?? throw new InvalidOperationException(
                                "An upsert mutation was not prepared.");
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            changed: true);
                        break;
                    case MemoryMutationKind.Delete:
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            staged.Remove(mutation.MemoryId));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown memory mutation kind "
                            + $"'{mutation.Kind}'.");
                }
            }

            if (staged.Count > _capacity)
            {
                throw new RuntimeContentLimitException(
                    nameof(snapshot),
                    "memory_capacity_exceeded",
                    $"Memory capacity exceeds {_capacity} records.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _records = staged;
            return new ReadOnlyCollection<MemoryMutationResult>(results);
        }
    }
}
