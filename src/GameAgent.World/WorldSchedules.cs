using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class WorldScheduleReasonCodes
{
    public const string Created = "world_schedule_created";
    public const string Rescheduled = "world_schedule_rescheduled";
    public const string Cancelled = "world_schedule_cancelled";
    public const string Claimed = "world_schedule_claimed";
    public const string Released = "world_schedule_released";
    public const string Completed = "world_schedule_completed";
    public const string Reassigned = "world_schedule_reassigned";
    public const string NotFound = "world_schedule_not_found";
    public const string AlreadyExists = "world_schedule_already_exists";
    public const string GenerationMismatch =
        "world_schedule_generation_mismatch";
    public const string IdempotencyConflict =
        "world_schedule_idempotency_conflict";
    public const string NotActive = "world_schedule_not_active";
    public const string ClaimedByAnother =
        "world_schedule_claimed_by_another";
    public const string NotDue = "world_schedule_not_due";
    public const string ClockMismatch = "world_schedule_clock_mismatch";
    public const string OccurrenceMismatch =
        "world_schedule_occurrence_mismatch";
    public const string ClaimLost = "world_schedule_claim_lost";
    public const string StaleOwner = "world_schedule_stale_owner";
    public const string TimelineNotFound =
        "world_schedule_timeline_not_found";
    public const string CapacityExceeded =
        "world_schedule_capacity_exceeded";
    public const string CorruptStore = "world_schedule_store_corrupt";
}

public sealed class WorldScheduleStoreException : Exception
{
    public WorldScheduleStoreException(
        string reasonCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
    }

    public string ReasonCode { get; }
}

public sealed class WorldScheduleStoreOptions
{
    public WorldScheduleStoreOptions(
        int maxSchedules = 4_096,
        int maxOperations = 16_384,
        long maxAggregatePayloadBytes = 16L * 1024 * 1024)
    {
        MaxSchedules = InRange(
            maxSchedules,
            1,
            100_000,
            nameof(maxSchedules));
        MaxOperations = InRange(
            maxOperations,
            1,
            100_000,
            nameof(maxOperations));
        if (maxAggregatePayloadBytes is < 1
            or > WorldPackageLimits.HardMaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAggregatePayloadBytes));
        }

        MaxAggregatePayloadBytes = maxAggregatePayloadBytes;
    }

    public int MaxSchedules { get; }

    public int MaxOperations { get; }

    public long MaxAggregatePayloadBytes { get; }

    private static int InRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public enum WorldScheduleStatus
{
    Active = 0,
    Cancelled = 1,
    Completed = 2
}

public enum WorldScheduleOperationKind
{
    Create = 0,
    Reschedule = 1,
    Cancel = 2,
    Claim = 3,
    Release = 4,
    Complete = 5,
    Reassign = 6
}

/// <summary>
/// Immutable long-term intent. It names when an opaque, schema-validated
/// payload becomes due; it does not define any game-specific effect.
/// </summary>
public sealed class WorldScheduleIntent
{
    private static readonly JsonValueLimits PayloadLimits = new(
        maxUtf8Bytes: 262_144,
        maxDepth: 48,
        maxNodes: 16_384,
        maxStringUtf8Bytes: 65_536,
        maxContainerItems: 4_096);

    private readonly JsonElement _payloadSchema;
    private readonly JsonElement _payload;

    public WorldScheduleIntent(
        string scheduleId,
        WorldTransactionScope scope,
        GameTimePoint dueAt,
        GameEntityIdentity owner,
        string payloadSchemaId,
        string payloadSchemaVersion,
        JsonElement payloadSchema,
        JsonElement payload)
    {
        ScheduleId = WorldValidation.Required(
            scheduleId,
            nameof(scheduleId),
            192);
        Scope = scope
                ?? throw new ArgumentNullException(nameof(scope));
        DueAt = dueAt
                ?? throw new ArgumentNullException(nameof(dueAt));
        if (!MatchesScope(DueAt, Scope))
        {
            throw new ArgumentException(
                "The due point must use the schedule timeline and epoch.",
                nameof(dueAt));
        }

        Owner = owner
                ?? throw new ArgumentNullException(nameof(owner));
        PayloadSchemaId = WorldValidation.Required(
            payloadSchemaId,
            nameof(payloadSchemaId),
            192);
        PayloadSchemaVersion = WorldValidation.Required(
            payloadSchemaVersion,
            nameof(payloadSchemaVersion),
            96);
        var schemaBytes = JsonValueInspector.ValidateAndMeasure(
            payloadSchema,
            PayloadLimits,
            nameof(payloadSchema));
        var payloadBytes = JsonValueInspector.ValidateAndMeasure(
            payload,
            PayloadLimits,
            nameof(payload));
        var validation = new ToolArgumentValidator().Validate(
            payloadSchema,
            payload);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "The schedule payload does not satisfy its schema.",
                nameof(payload));
        }

        _payloadSchema = payloadSchema.Clone();
        _payload = payload.Clone();
        PayloadUtf8Bytes = checked(schemaBytes + payloadBytes);
        PayloadSchemaDigest =
            CanonicalJsonDigest.ComputeSha256(_payloadSchema);
        PayloadDigest = CanonicalJsonDigest.ComputeSha256(_payload);
        SemanticDigest = WorldScheduleIdentity.IntentDigest(this);
    }

    public string ScheduleId { get; }

    public WorldTransactionScope Scope { get; }

    public GameTimePoint DueAt { get; }

    public GameEntityIdentity Owner { get; }

    public string PayloadSchemaId { get; }

    public string PayloadSchemaVersion { get; }

    public JsonElement PayloadSchema => _payloadSchema.Clone();

    public string PayloadSchemaDigest { get; }

    public JsonElement Payload => _payload.Clone();

    public string PayloadDigest { get; }

    public int PayloadUtf8Bytes { get; }

    public string SemanticDigest { get; }

    internal string StableKey =>
        WorldValidation.ComposeStableKey(
            Scope.StableKey,
            ScheduleId);

    internal WorldScheduleIntent WithDueAt(GameTimePoint dueAt)
    {
        return new WorldScheduleIntent(
            ScheduleId,
            Scope,
            dueAt,
            Owner,
            PayloadSchemaId,
            PayloadSchemaVersion,
            _payloadSchema,
            _payload);
    }

    internal WorldScheduleIntent Rehome(
        WorldTransactionScope scope)
    {
        return new WorldScheduleIntent(
            ScheduleId,
            scope,
            new GameTimePoint(
                DueAt.ClockId,
                scope.TimelineId,
                scope.TimelineEpoch,
                DueAt.Tick),
            Owner,
            PayloadSchemaId,
            PayloadSchemaVersion,
            _payloadSchema,
            _payload);
    }

    internal static bool MatchesScope(
        GameTimePoint point,
        WorldTransactionScope scope)
    {
        return string.Equals(
                   point.TimelineId,
                   scope.TimelineId,
                   StringComparison.Ordinal)
               && point.Epoch == scope.TimelineEpoch;
    }
}

/// <summary>
/// Durable single-owner claim for one occurrence. The token is a coordination
/// capability, not an authentication secret. Claims have no implicit timeout;
/// recovery must reconcile downstream state before explicit reassignment.
/// </summary>
public sealed class WorldScheduleClaim
{
    internal WorldScheduleClaim(
        string claimantId,
        string claimToken,
        string operationId)
    {
        ClaimantId = WorldValidation.Required(
            claimantId,
            nameof(claimantId),
            192);
        ClaimToken = WorldValidation.Required(
            claimToken,
            nameof(claimToken),
            192);
        OperationId = WorldValidation.Required(
            operationId,
            nameof(operationId),
            192);
    }

    public string ClaimantId { get; }

    public string ClaimToken { get; }

    public string OperationId { get; }

    internal bool Matches(string claimantId, string claimToken)
    {
        return string.Equals(
                   ClaimantId,
                   claimantId,
                   StringComparison.Ordinal)
               && string.Equals(
                   ClaimToken,
                   claimToken,
                   StringComparison.Ordinal);
    }

    internal bool IsValidFor(string occurrenceId)
    {
        return string.Equals(
            ClaimToken,
            WorldScheduleIdentity.ClaimToken(
                occurrenceId,
                ClaimantId,
                OperationId),
            StringComparison.Ordinal);
    }

    internal bool IsSameAs(WorldScheduleClaim other)
    {
        return other is not null
               && string.Equals(
                   ClaimantId,
                   other.ClaimantId,
                   StringComparison.Ordinal)
               && string.Equals(
                   ClaimToken,
                   other.ClaimToken,
                   StringComparison.Ordinal)
               && string.Equals(
                   OperationId,
                   other.OperationId,
                   StringComparison.Ordinal);
    }
}

public sealed class WorldScheduleRecord
{
    internal WorldScheduleRecord(
        WorldScheduleIntent intent,
        long generation,
        WorldScheduleStatus status,
        WorldScheduleClaim? claim)
    {
        Intent = intent
                 ?? throw new ArgumentNullException(nameof(intent));
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (!Enum.IsDefined(typeof(WorldScheduleStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (status != WorldScheduleStatus.Active
            && claim is not null)
        {
            throw new ArgumentException(
                "Only an active schedule can have a claim.",
                nameof(claim));
        }

        Generation = generation;
        Status = status;
        Claim = claim;
        OccurrenceId = WorldScheduleIdentity.OccurrenceId(
            intent.Scope,
            intent.ScheduleId,
            generation);
        if (claim is not null
            && !claim.IsValidFor(OccurrenceId))
        {
            throw new ArgumentException(
                "The schedule claim token does not match its occurrence.",
                nameof(claim));
        }

        RecordDigest = WorldScheduleIdentity.RecordDigest(this);
    }

    public WorldScheduleIntent Intent { get; }

    public string ScheduleId => Intent.ScheduleId;

    public WorldTransactionScope Scope => Intent.Scope;

    public GameTimePoint DueAt => Intent.DueAt;

    public GameEntityIdentity Owner => Intent.Owner;

    public long Generation { get; }

    public WorldScheduleStatus Status { get; }

    public string OccurrenceId { get; }

    public WorldScheduleClaim? Claim { get; }

    public string RecordDigest { get; }

    internal string StableKey => Intent.StableKey;

    internal WorldScheduleRecord WithDueAt(GameTimePoint dueAt)
    {
        if (Generation == long.MaxValue)
        {
            throw new WorldScheduleStoreException(
                WorldScheduleReasonCodes.CapacityExceeded,
                "The schedule generation cannot advance.");
        }

        return new WorldScheduleRecord(
            Intent.WithDueAt(dueAt),
            Generation + 1,
            WorldScheduleStatus.Active,
            claim: null);
    }

    internal WorldScheduleRecord WithStatus(
        WorldScheduleStatus status)
    {
        return new WorldScheduleRecord(
            Intent,
            Generation,
            status,
            claim: null);
    }

    internal WorldScheduleRecord WithClaim(
        WorldScheduleClaim? claim)
    {
        return new WorldScheduleRecord(
            Intent,
            Generation,
            Status,
            claim);
    }

    internal WorldScheduleRecord Rehome(
        WorldTransactionScope scope)
    {
        return new WorldScheduleRecord(
            Intent.Rehome(scope),
            Generation,
            Status,
            claim: null);
    }
}

/// <summary>
/// One closed, idempotent schedule mutation. Factory methods enforce the
/// field shape for each operation kind.
/// </summary>
public sealed class WorldScheduleCommand
{
    private WorldScheduleCommand(
        string operationId,
        WorldScheduleOperationKind kind,
        WorldTransactionScope scope,
        string scheduleId,
        long? expectedGeneration,
        WorldScheduleIntent? createIntent,
        GameTimePoint? dueAt,
        GameTimePoint? observedAt,
        string? occurrenceId,
        string? claimantId,
        string? claimToken,
        string? replacementClaimantId)
    {
        OperationId = WorldValidation.Required(
            operationId,
            nameof(operationId),
            192);
        if (!Enum.IsDefined(typeof(WorldScheduleOperationKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Scope = scope
                ?? throw new ArgumentNullException(nameof(scope));
        ScheduleId = WorldValidation.Required(
            scheduleId,
            nameof(scheduleId),
            192);
        if (expectedGeneration is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedGeneration));
        }

        ExpectedGeneration = expectedGeneration;
        CreateIntent = createIntent;
        DueAt = dueAt;
        ObservedAt = observedAt;
        OccurrenceId = WorldValidation.Optional(
            occurrenceId,
            nameof(occurrenceId),
            192);
        ClaimantId = WorldValidation.Optional(
            claimantId,
            nameof(claimantId),
            192);
        ClaimToken = WorldValidation.Optional(
            claimToken,
            nameof(claimToken),
            192);
        ReplacementClaimantId = WorldValidation.Optional(
            replacementClaimantId,
            nameof(replacementClaimantId),
            192);
        ValidateShape();
        RequestFingerprint =
            WorldScheduleIdentity.CommandFingerprint(this);
    }

    public string OperationId { get; }

    public WorldScheduleOperationKind Kind { get; }

    public WorldTransactionScope Scope { get; }

    public string ScheduleId { get; }

    public long? ExpectedGeneration { get; }

    public WorldScheduleIntent? CreateIntent { get; }

    public GameTimePoint? DueAt { get; }

    public GameTimePoint? ObservedAt { get; }

    public string? OccurrenceId { get; }

    public string? ClaimantId { get; }

    public string? ClaimToken { get; }

    public string? ReplacementClaimantId { get; }

    public string RequestFingerprint { get; }

    internal string ScopedOperationKey =>
        WorldValidation.ComposeStableKey(
            Scope.StableKey,
            OperationId);

    internal string ScheduleKey =>
        WorldValidation.ComposeStableKey(
            Scope.StableKey,
            ScheduleId);

    public static WorldScheduleCommand Create(
        string operationId,
        WorldScheduleIntent intent)
    {
        if (intent is null)
        {
            throw new ArgumentNullException(nameof(intent));
        }

        return new WorldScheduleCommand(
            operationId,
            WorldScheduleOperationKind.Create,
            intent.Scope,
            intent.ScheduleId,
            expectedGeneration: null,
            intent,
            dueAt: null,
            observedAt: null,
            occurrenceId: null,
            claimantId: null,
            claimToken: null,
            replacementClaimantId: null);
    }

    public static WorldScheduleCommand Reschedule(
        string operationId,
        WorldTransactionScope scope,
        string scheduleId,
        long expectedGeneration,
        GameTimePoint dueAt)
    {
        return new WorldScheduleCommand(
            operationId,
            WorldScheduleOperationKind.Reschedule,
            scope,
            scheduleId,
            expectedGeneration,
            createIntent: null,
            dueAt,
            observedAt: null,
            occurrenceId: null,
            claimantId: null,
            claimToken: null,
            replacementClaimantId: null);
    }

    public static WorldScheduleCommand Cancel(
        string operationId,
        WorldTransactionScope scope,
        string scheduleId,
        long expectedGeneration)
    {
        return Simple(
            operationId,
            WorldScheduleOperationKind.Cancel,
            scope,
            scheduleId,
            expectedGeneration);
    }

    public static WorldScheduleCommand Claim(
        string operationId,
        WorldTransactionScope scope,
        string scheduleId,
        long expectedGeneration,
        GameTimePoint observedAt,
        string claimantId)
    {
        return new WorldScheduleCommand(
            operationId,
            WorldScheduleOperationKind.Claim,
            scope,
            scheduleId,
            expectedGeneration,
            createIntent: null,
            dueAt: null,
            observedAt,
            occurrenceId: null,
            claimantId,
            claimToken: null,
            replacementClaimantId: null);
    }

    public static WorldScheduleCommand Release(
        string operationId,
        WorldTransactionScope scope,
        string scheduleId,
        long expectedGeneration,
        string occurrenceId,
        string claimantId,
        string claimToken)
    {
        return ClaimMutation(
            operationId,
            WorldScheduleOperationKind.Release,
            scope,
            scheduleId,
            expectedGeneration,
            occurrenceId,
            claimantId,
            claimToken);
    }

    public static WorldScheduleCommand Complete(
        string operationId,
        WorldTransactionScope scope,
        string scheduleId,
        long expectedGeneration,
        string occurrenceId,
        string claimantId,
        string claimToken)
    {
        return ClaimMutation(
            operationId,
            WorldScheduleOperationKind.Complete,
            scope,
            scheduleId,
            expectedGeneration,
            occurrenceId,
            claimantId,
            claimToken);
    }

    public static WorldScheduleCommand Reassign(
        string operationId,
        WorldTransactionScope scope,
        string scheduleId,
        long expectedGeneration,
        string occurrenceId,
        string expectedClaimantId,
        string replacementClaimantId)
    {
        return new WorldScheduleCommand(
            operationId,
            WorldScheduleOperationKind.Reassign,
            scope,
            scheduleId,
            expectedGeneration,
            createIntent: null,
            dueAt: null,
            observedAt: null,
            occurrenceId,
            expectedClaimantId,
            claimToken: null,
            replacementClaimantId);
    }

    private static WorldScheduleCommand Simple(
        string operationId,
        WorldScheduleOperationKind kind,
        WorldTransactionScope scope,
        string scheduleId,
        long expectedGeneration)
    {
        return new WorldScheduleCommand(
            operationId,
            kind,
            scope,
            scheduleId,
            expectedGeneration,
            createIntent: null,
            dueAt: null,
            observedAt: null,
            occurrenceId: null,
            claimantId: null,
            claimToken: null,
            replacementClaimantId: null);
    }

    private static WorldScheduleCommand ClaimMutation(
        string operationId,
        WorldScheduleOperationKind kind,
        WorldTransactionScope scope,
        string scheduleId,
        long expectedGeneration,
        string occurrenceId,
        string claimantId,
        string claimToken)
    {
        return new WorldScheduleCommand(
            operationId,
            kind,
            scope,
            scheduleId,
            expectedGeneration,
            createIntent: null,
            dueAt: null,
            observedAt: null,
            occurrenceId,
            claimantId,
            claimToken,
            replacementClaimantId: null);
    }

    private void ValidateShape()
    {
        if (CreateIntent is not null
            && (!CreateIntent.Scope.IsSameAs(Scope)
                || !string.Equals(
                    CreateIntent.ScheduleId,
                    ScheduleId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The create intent must match the command scope.");
        }

        if (DueAt is not null
            && !WorldScheduleIntent.MatchesScope(DueAt, Scope)
            || ObservedAt is not null
            && !WorldScheduleIntent.MatchesScope(ObservedAt, Scope))
        {
            throw new ArgumentException(
                "Schedule time points must match the command scope.");
        }

        var valid = Kind switch
        {
            WorldScheduleOperationKind.Create =>
                CreateIntent is not null
                && !ExpectedGeneration.HasValue
                && DueAt is null
                && ObservedAt is null
                && OccurrenceId is null
                && ClaimantId is null
                && ClaimToken is null
                && ReplacementClaimantId is null,
            WorldScheduleOperationKind.Reschedule =>
                CreateIntent is null
                && ExpectedGeneration.HasValue
                && DueAt is not null
                && ObservedAt is null
                && OccurrenceId is null
                && ClaimantId is null
                && ClaimToken is null
                && ReplacementClaimantId is null,
            WorldScheduleOperationKind.Cancel =>
                HasOnlyExpectedGeneration(),
            WorldScheduleOperationKind.Claim =>
                CreateIntent is null
                && ExpectedGeneration.HasValue
                && DueAt is null
                && ObservedAt is not null
                && OccurrenceId is null
                && ClaimantId is not null
                && ClaimToken is null
                && ReplacementClaimantId is null,
            WorldScheduleOperationKind.Release
                or WorldScheduleOperationKind.Complete =>
                CreateIntent is null
                && ExpectedGeneration.HasValue
                && DueAt is null
                && ObservedAt is null
                && OccurrenceId is not null
                && ClaimantId is not null
                && ClaimToken is not null
                && ReplacementClaimantId is null,
            WorldScheduleOperationKind.Reassign =>
                CreateIntent is null
                && ExpectedGeneration.HasValue
                && DueAt is null
                && ObservedAt is null
                && OccurrenceId is not null
                && ClaimantId is not null
                && ClaimToken is null
                && ReplacementClaimantId is not null,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The schedule command fields do not match its kind.");
        }
    }

    private bool HasOnlyExpectedGeneration()
    {
        return CreateIntent is null
               && ExpectedGeneration.HasValue
               && DueAt is null
               && ObservedAt is null
               && OccurrenceId is null
               && ClaimantId is null
               && ClaimToken is null
               && ReplacementClaimantId is null;
    }
}

public sealed class WorldScheduleOperationReceipt
{
    internal WorldScheduleOperationReceipt(
        WorldTransactionScope scope,
        string scheduleId,
        string operationId,
        WorldScheduleOperationKind kind,
        string requestFingerprint,
        bool applied,
        string outcomeCode,
        long? resultingGeneration,
        WorldScheduleStatus? resultingStatus,
        string? occurrenceId,
        WorldScheduleClaim? claim)
    {
        Scope = scope
                ?? throw new ArgumentNullException(nameof(scope));
        ScheduleId = WorldValidation.Required(
            scheduleId,
            nameof(scheduleId),
            192);
        OperationId = WorldValidation.Required(
            operationId,
            nameof(operationId),
            192);
        if (!Enum.IsDefined(typeof(WorldScheduleOperationKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!CanonicalJsonDigest.IsSha256(requestFingerprint))
        {
            throw new ArgumentException(
                "Request fingerprint must be a lowercase SHA-256.",
                nameof(requestFingerprint));
        }

        if (resultingGeneration is < 0
            || resultingStatus.HasValue
            && !Enum.IsDefined(
                typeof(WorldScheduleStatus),
                resultingStatus.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultingGeneration));
        }

        Kind = kind;
        RequestFingerprint = requestFingerprint;
        Applied = applied;
        OutcomeCode = WorldValidation.Required(
            outcomeCode,
            nameof(outcomeCode),
            96);
        ResultingGeneration = resultingGeneration;
        ResultingStatus = resultingStatus;
        OccurrenceId = WorldValidation.Optional(
            occurrenceId,
            nameof(occurrenceId),
            192);
        Claim = claim;
        var hasResult = resultingGeneration.HasValue;
        var appliedKindRequiresClaim =
            Kind is (
                WorldScheduleOperationKind.Claim
                or WorldScheduleOperationKind.Reassign);
        var expectedOccurrence = hasResult
            ? WorldScheduleIdentity.OccurrenceId(
                Scope,
                ScheduleId,
                resultingGeneration!.Value)
            : null;
        if (hasResult != resultingStatus.HasValue
            || hasResult != (OccurrenceId is not null)
            || Claim is not null && !hasResult
            || Applied && !hasResult
            || Applied
            && appliedKindRequiresClaim != (Claim is not null)
            || hasResult
            && !string.Equals(
                expectedOccurrence,
                OccurrenceId,
                StringComparison.Ordinal)
            || Claim is not null
            && (ResultingStatus
                    != WorldScheduleStatus.Active
                || !Claim.IsValidFor(OccurrenceId!)
                || Applied
                && appliedKindRequiresClaim
                && !string.Equals(
                    Claim.OperationId,
                    OperationId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The schedule receipt result fields are inconsistent.");
        }

        ReceiptId = WorldScheduleIdentity.ReceiptId(this);
    }

    public WorldTransactionScope Scope { get; }

    public string ScheduleId { get; }

    public string OperationId { get; }

    public WorldScheduleOperationKind Kind { get; }

    public string RequestFingerprint { get; }

    public bool Applied { get; }

    public string OutcomeCode { get; }

    public long? ResultingGeneration { get; }

    public WorldScheduleStatus? ResultingStatus { get; }

    public string? OccurrenceId { get; }

    public WorldScheduleClaim? Claim { get; }

    public string ReceiptId { get; }

    internal string ScopedOperationKey =>
        WorldValidation.ComposeStableKey(
            Scope.StableKey,
            OperationId);

    internal bool EstablishesClaim(WorldScheduleRecord schedule)
    {
        return schedule is not null
               && Applied
               && Kind is (
                   WorldScheduleOperationKind.Claim
                   or WorldScheduleOperationKind.Reassign)
               && Scope.IsSameAs(schedule.Scope)
               && string.Equals(
                   ScheduleId,
                   schedule.ScheduleId,
                   StringComparison.Ordinal)
               && ResultingGeneration == schedule.Generation
               && ResultingStatus == WorldScheduleStatus.Active
               && string.Equals(
                   OccurrenceId,
                   schedule.OccurrenceId,
                   StringComparison.Ordinal)
               && Claim is not null
               && schedule.Claim is not null
               && Claim.IsSameAs(schedule.Claim);
    }
}

public sealed class WorldScheduleMutationResult
{
    internal WorldScheduleMutationResult(
        string reasonCode,
        WorldScheduleOperationReceipt? receipt,
        WorldScheduleRecord? schedule,
        bool replay)
    {
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        Receipt = receipt;
        Schedule = schedule;
        IsReplay = replay;
    }

    public string ReasonCode { get; }

    public WorldScheduleOperationReceipt? Receipt { get; }

    public WorldScheduleRecord? Schedule { get; }

    public bool IsReplay { get; }

    public bool Applied => Receipt?.Applied == true;
}

public sealed class WorldScheduleDueCursor
{
    public WorldScheduleDueCursor(
        long dueTick,
        string scheduleId,
        long generation)
    {
        DueTick = dueTick;
        ScheduleId = WorldValidation.Required(
            scheduleId,
            nameof(scheduleId),
            192);
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        Generation = generation;
    }

    public long DueTick { get; }

    public string ScheduleId { get; }

    public long Generation { get; }
}

public sealed class WorldScheduleDueQuery
{
    public WorldScheduleDueQuery(
        WorldTransactionScope scope,
        string clockId,
        long throughTick,
        int maximumResults = 256,
        WorldScheduleDueCursor? after = null)
    {
        Scope = scope
                ?? throw new ArgumentNullException(nameof(scope));
        ClockId = WorldValidation.Required(
            clockId,
            nameof(clockId),
            128);
        if (maximumResults is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResults));
        }

        ThroughTick = throughTick;
        MaximumResults = maximumResults;
        After = after;
    }

    public WorldTransactionScope Scope { get; }

    public string ClockId { get; }

    public long ThroughTick { get; }

    public int MaximumResults { get; }

    public WorldScheduleDueCursor? After { get; }
}

public sealed class WorldScheduleDuePage
{
    internal WorldScheduleDuePage(
        IReadOnlyList<WorldScheduleRecord> items,
        WorldScheduleDueCursor? next)
    {
        Items = items;
        Next = next;
    }

    public IReadOnlyList<WorldScheduleRecord> Items { get; }

    public WorldScheduleDueCursor? Next { get; }
}

public interface IWorldScheduleStore
{
    ValueTask<WorldScheduleMutationResult> ExecuteAsync(
        WorldScheduleCommand command,
        CancellationToken cancellationToken);

    ValueTask<WorldScheduleRecord?> FindAsync(
        WorldTransactionScope scope,
        string scheduleId,
        CancellationToken cancellationToken);

    ValueTask<WorldScheduleDuePage> QueryDueAsync(
        WorldScheduleDueQuery query,
        CancellationToken cancellationToken);
}

internal static class WorldScheduleStoreLogic
{
    public static void EnsureCapacity(
        IEnumerable<WorldScheduleRecord> schedules,
        int operationCount,
        WorldScheduleStoreOptions options)
    {
        if (operationCount > options.MaxOperations)
        {
            throw Capacity(
                "The schedule operation capacity has been exceeded.");
        }

        var count = 0;
        long payloadBytes = 0;
        foreach (var schedule in schedules)
        {
            count++;
            if (count > options.MaxSchedules)
            {
                throw Capacity(
                    "The schedule capacity has been exceeded.");
            }

            payloadBytes = checked(
                payloadBytes
                + schedule.Intent.PayloadUtf8Bytes);
            if (payloadBytes > options.MaxAggregatePayloadBytes)
            {
                throw Capacity(
                    "The aggregate schedule payload capacity has been exceeded.");
            }
        }
    }

    public static WorldScheduleMutationResult Execute(
        IDictionary<string, WorldScheduleRecord> schedules,
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        IReadOnlyDictionary<string, WorldAuthoritativeStateSnapshot>
            states,
        WorldScheduleCommand command,
        WorldScheduleStoreOptions options)
    {
        if (operations.TryGetValue(
                command.ScopedOperationKey,
                out var existing))
        {
            if (!string.Equals(
                    existing.RequestFingerprint,
                    command.RequestFingerprint,
                    StringComparison.Ordinal))
            {
                return new WorldScheduleMutationResult(
                    WorldScheduleReasonCodes.IdempotencyConflict,
                    receipt: null,
                    Current(schedules, command.ScheduleKey),
                    replay: false);
            }

            return new WorldScheduleMutationResult(
                existing.OutcomeCode,
                existing,
                Current(schedules, command.ScheduleKey),
                replay: true);
        }

        if (operations.Count >= options.MaxOperations)
        {
            throw Capacity(
                "The schedule operation capacity has been reached.");
        }

        schedules.TryGetValue(
            command.ScheduleKey,
            out var current);
        if (!TimelineExists(states, command.Scope))
        {
            return Record(
                operations,
                command,
                applied: false,
                WorldScheduleReasonCodes.TimelineNotFound,
                current);
        }

        return command.Kind switch
        {
            WorldScheduleOperationKind.Create => Create(
                schedules,
                operations,
                states,
                command,
                current,
                options),
            WorldScheduleOperationKind.Reschedule => Reschedule(
                schedules,
                operations,
                command,
                current),
            WorldScheduleOperationKind.Cancel => Cancel(
                schedules,
                operations,
                command,
                current),
            WorldScheduleOperationKind.Claim => Claim(
                schedules,
                operations,
                states,
                command,
                current),
            WorldScheduleOperationKind.Release => Release(
                schedules,
                operations,
                command,
                current),
            WorldScheduleOperationKind.Complete => Complete(
                schedules,
                operations,
                command,
                current),
            WorldScheduleOperationKind.Reassign => Reassign(
                schedules,
                operations,
                states,
                command,
                current),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }

    public static WorldScheduleDuePage QueryDue(
        IEnumerable<WorldScheduleRecord> schedules,
        WorldScheduleDueQuery query,
        CancellationToken cancellationToken)
    {
        var ordered = new List<WorldScheduleRecord>();
        foreach (var schedule in schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (schedule.Status == WorldScheduleStatus.Active
                && schedule.Scope.IsSameAs(query.Scope)
                && string.Equals(
                    schedule.DueAt.ClockId,
                    query.ClockId,
                    StringComparison.Ordinal)
                && schedule.DueAt.Tick <= query.ThroughTick
                && IsAfter(schedule, query.After))
            {
                ordered.Add(schedule);
            }
        }

        ordered.Sort(Compare);
        var hasMore = ordered.Count > query.MaximumResults;
        var page = ordered.Take(query.MaximumResults).ToArray();
        WorldScheduleDueCursor? next = null;
        if (hasMore && page.Length > 0)
        {
            var last = page[page.Length - 1];
            next = new WorldScheduleDueCursor(
                last.DueAt.Tick,
                last.ScheduleId,
                last.Generation);
        }

        return new WorldScheduleDuePage(
            new ReadOnlyCollection<WorldScheduleRecord>(page),
            next);
    }

    private static WorldScheduleMutationResult Create(
        IDictionary<string, WorldScheduleRecord> schedules,
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        IReadOnlyDictionary<string, WorldAuthoritativeStateSnapshot>
            states,
        WorldScheduleCommand command,
        WorldScheduleRecord? current,
        WorldScheduleStoreOptions options)
    {
        if (current is not null)
        {
            return Record(
                operations,
                command,
                applied: false,
                WorldScheduleReasonCodes.AlreadyExists,
                current);
        }

        var intent = command.CreateIntent!;
        var ownerReason = OwnerReason(states, intent);
        if (ownerReason is not null)
        {
            return Record(
                operations,
                command,
                applied: false,
                ownerReason,
                schedule: null);
        }

        if (schedules.Count >= options.MaxSchedules)
        {
            throw Capacity("The schedule capacity has been reached.");
        }

        long payloadBytes = intent.PayloadUtf8Bytes;
        if (payloadBytes > options.MaxAggregatePayloadBytes)
        {
            throw Capacity(
                "The aggregate schedule payload capacity has been reached.");
        }

        foreach (var schedule in schedules.Values)
        {
            payloadBytes = checked(
                payloadBytes
                + schedule.Intent.PayloadUtf8Bytes);
            if (payloadBytes > options.MaxAggregatePayloadBytes)
            {
                throw Capacity(
                    "The aggregate schedule payload capacity has been reached.");
            }
        }

        var created = new WorldScheduleRecord(
            intent,
            generation: 0,
            WorldScheduleStatus.Active,
            claim: null);
        schedules.Add(created.StableKey, created);
        return Record(
            operations,
            command,
            applied: true,
            WorldScheduleReasonCodes.Created,
            created);
    }

    private static WorldScheduleMutationResult Reschedule(
        IDictionary<string, WorldScheduleRecord> schedules,
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var rejection = RescheduleReason(command, current);
        if (rejection is not null)
        {
            return Record(
                operations,
                command,
                applied: false,
                rejection,
                current);
        }

        var updated = current!.WithDueAt(command.DueAt!);
        schedules[updated.StableKey] = updated;
        return Record(
            operations,
            command,
            applied: true,
            WorldScheduleReasonCodes.Rescheduled,
            updated);
    }

    private static WorldScheduleMutationResult Cancel(
        IDictionary<string, WorldScheduleRecord> schedules,
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var rejection = MutableReason(command, current);
        if (rejection is not null)
        {
            return Record(
                operations,
                command,
                applied: false,
                rejection,
                current);
        }

        var updated = current!.WithStatus(
            WorldScheduleStatus.Cancelled);
        schedules[updated.StableKey] = updated;
        return Record(
            operations,
            command,
            applied: true,
            WorldScheduleReasonCodes.Cancelled,
            updated);
    }

    private static WorldScheduleMutationResult Claim(
        IDictionary<string, WorldScheduleRecord> schedules,
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        IReadOnlyDictionary<string, WorldAuthoritativeStateSnapshot>
            states,
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var rejection = BasicReason(command, current);
        if (rejection is null
            && current!.Status != WorldScheduleStatus.Active)
        {
            rejection = WorldScheduleReasonCodes.NotActive;
        }
        else if (rejection is null
                 && current!.Claim is not null)
        {
            rejection = WorldScheduleReasonCodes.ClaimedByAnother;
        }
        else if (rejection is null
                 && !string.Equals(
                     current!.DueAt.ClockId,
                     command.ObservedAt!.ClockId,
                     StringComparison.Ordinal))
        {
            rejection = WorldScheduleReasonCodes.ClockMismatch;
        }
        else if (rejection is null
                 && command.ObservedAt!.Tick < current!.DueAt.Tick)
        {
            rejection = WorldScheduleReasonCodes.NotDue;
        }
        else if (rejection is null)
        {
            rejection = OwnerReason(states, current!.Intent);
        }

        if (rejection is not null)
        {
            return Record(
                operations,
                command,
                applied: false,
                rejection,
                current);
        }

        var claim = new WorldScheduleClaim(
            command.ClaimantId!,
            WorldScheduleIdentity.ClaimToken(
                current!.OccurrenceId,
                command.ClaimantId!,
                command.OperationId),
            command.OperationId);
        var updated = current.WithClaim(claim);
        schedules[updated.StableKey] = updated;
        return Record(
            operations,
            command,
            applied: true,
            WorldScheduleReasonCodes.Claimed,
            updated);
    }

    private static WorldScheduleMutationResult Release(
        IDictionary<string, WorldScheduleRecord> schedules,
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var rejection = ClaimReason(command, current);
        if (rejection is not null)
        {
            return Record(
                operations,
                command,
                applied: false,
                rejection,
                current);
        }

        var updated = current!.WithClaim(claim: null);
        schedules[updated.StableKey] = updated;
        return Record(
            operations,
            command,
            applied: true,
            WorldScheduleReasonCodes.Released,
            updated);
    }

    private static WorldScheduleMutationResult Complete(
        IDictionary<string, WorldScheduleRecord> schedules,
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var rejection = ClaimReason(command, current);
        if (rejection is not null)
        {
            return Record(
                operations,
                command,
                applied: false,
                rejection,
                current);
        }

        var updated = current!.WithStatus(
            WorldScheduleStatus.Completed);
        schedules[updated.StableKey] = updated;
        return Record(
            operations,
            command,
            applied: true,
            WorldScheduleReasonCodes.Completed,
            updated);
    }

    private static WorldScheduleMutationResult Reassign(
        IDictionary<string, WorldScheduleRecord> schedules,
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        IReadOnlyDictionary<string, WorldAuthoritativeStateSnapshot>
            states,
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var rejection = BasicReason(command, current);
        if (rejection is null
            && current!.Status != WorldScheduleStatus.Active)
        {
            rejection = WorldScheduleReasonCodes.NotActive;
        }
        else if (rejection is null
                 && !string.Equals(
                     current!.OccurrenceId,
                     command.OccurrenceId,
                     StringComparison.Ordinal))
        {
            rejection = WorldScheduleReasonCodes.OccurrenceMismatch;
        }
        else if (rejection is null
                 && (current!.Claim is null
                     || !string.Equals(
                         current.Claim.ClaimantId,
                         command.ClaimantId,
                         StringComparison.Ordinal)))
        {
            rejection = WorldScheduleReasonCodes.ClaimLost;
        }
        else if (rejection is null)
        {
            rejection = OwnerReason(states, current!.Intent);
        }

        if (rejection is not null)
        {
            return Record(
                operations,
                command,
                applied: false,
                rejection,
                current);
        }

        var replacement = new WorldScheduleClaim(
            command.ReplacementClaimantId!,
            WorldScheduleIdentity.ClaimToken(
                current!.OccurrenceId,
                command.ReplacementClaimantId!,
                command.OperationId),
            command.OperationId);
        var updated = current.WithClaim(replacement);
        schedules[updated.StableKey] = updated;
        return Record(
            operations,
            command,
            applied: true,
            WorldScheduleReasonCodes.Reassigned,
            updated);
    }

    private static string? MutableReason(
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var basic = BasicReason(command, current);
        if (basic is not null)
        {
            return basic;
        }

        if (current!.Status != WorldScheduleStatus.Active)
        {
            return WorldScheduleReasonCodes.NotActive;
        }

        return current.Claim is not null
            ? WorldScheduleReasonCodes.ClaimedByAnother
            : null;
    }

    private static string? RescheduleReason(
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var basic = BasicReason(command, current);
        if (basic is not null)
        {
            return basic;
        }

        if (current!.Status == WorldScheduleStatus.Cancelled)
        {
            return WorldScheduleReasonCodes.NotActive;
        }

        return current.Claim is not null
            ? WorldScheduleReasonCodes.ClaimedByAnother
            : null;
    }

    private static string? BasicReason(
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        if (current is null)
        {
            return WorldScheduleReasonCodes.NotFound;
        }

        return current.Generation != command.ExpectedGeneration
            ? WorldScheduleReasonCodes.GenerationMismatch
            : null;
    }

    private static string? ClaimReason(
        WorldScheduleCommand command,
        WorldScheduleRecord? current)
    {
        var basic = BasicReason(command, current);
        if (basic is not null)
        {
            return basic;
        }

        if (current!.Status != WorldScheduleStatus.Active)
        {
            return WorldScheduleReasonCodes.NotActive;
        }

        if (!string.Equals(
                current.OccurrenceId,
                command.OccurrenceId,
                StringComparison.Ordinal))
        {
            return WorldScheduleReasonCodes.OccurrenceMismatch;
        }

        return current.Claim is null
               || !current.Claim.Matches(
                   command.ClaimantId!,
                   command.ClaimToken!)
            ? WorldScheduleReasonCodes.ClaimLost
            : null;
    }

    private static string? OwnerReason(
        IReadOnlyDictionary<string, WorldAuthoritativeStateSnapshot>
            states,
        WorldScheduleIntent intent)
    {
        if (!TryTimeline(
                states,
                intent.Scope,
                out var snapshot))
        {
            return WorldScheduleReasonCodes.TimelineNotFound;
        }

        return !snapshot!.TryGetIncarnation(
                   intent.Owner.EntityId,
                   out var incarnation)
               || incarnation != intent.Owner.Incarnation
            ? WorldScheduleReasonCodes.StaleOwner
            : null;
    }

    private static bool TimelineExists(
        IReadOnlyDictionary<string, WorldAuthoritativeStateSnapshot>
            states,
        WorldTransactionScope scope)
    {
        return TryTimeline(states, scope, out _);
    }

    private static bool TryTimeline(
        IReadOnlyDictionary<string, WorldAuthoritativeStateSnapshot>
            states,
        WorldTransactionScope scope,
        out WorldAuthoritativeStateSnapshot? snapshot)
    {
        var address = new WorldTimelineAddress(
            scope.WorldId,
            scope.TimelineId);
        return states.TryGetValue(address.StableKey, out snapshot)
               && snapshot.Coordinate.TimelineEpoch
               == scope.TimelineEpoch;
    }

    private static WorldScheduleMutationResult Record(
        IDictionary<string, WorldScheduleOperationReceipt> operations,
        WorldScheduleCommand command,
        bool applied,
        string outcomeCode,
        WorldScheduleRecord? schedule)
    {
        var receipt = new WorldScheduleOperationReceipt(
            command.Scope,
            command.ScheduleId,
            command.OperationId,
            command.Kind,
            command.RequestFingerprint,
            applied,
            outcomeCode,
            schedule?.Generation,
            schedule?.Status,
            schedule?.OccurrenceId,
            schedule?.Claim);
        operations.Add(receipt.ScopedOperationKey, receipt);
        return new WorldScheduleMutationResult(
            outcomeCode,
            receipt,
            schedule,
            replay: false);
    }

    private static WorldScheduleRecord? Current(
        IDictionary<string, WorldScheduleRecord> schedules,
        string key)
    {
        schedules.TryGetValue(key, out var value);
        return value;
    }

    private static bool IsAfter(
        WorldScheduleRecord record,
        WorldScheduleDueCursor? cursor)
    {
        return cursor is null || Compare(
            record,
            cursor.DueTick,
            cursor.ScheduleId,
            cursor.Generation) > 0;
    }

    private static int Compare(
        WorldScheduleRecord left,
        WorldScheduleRecord right)
    {
        return Compare(
            left,
            right.DueAt.Tick,
            right.ScheduleId,
            right.Generation);
    }

    private static int Compare(
        WorldScheduleRecord left,
        long dueTick,
        string scheduleId,
        long generation)
    {
        var tick = left.DueAt.Tick.CompareTo(dueTick);
        if (tick != 0)
        {
            return tick;
        }

        var schedule = string.CompareOrdinal(
            left.ScheduleId,
            scheduleId);
        return schedule != 0
            ? schedule
            : left.Generation.CompareTo(generation);
    }

    private static WorldScheduleStoreException Capacity(string message)
    {
        return new WorldScheduleStoreException(
            WorldScheduleReasonCodes.CapacityExceeded,
            message);
    }
}

internal static class WorldScheduleIdentity
{
    public static string OccurrenceId(
        WorldTransactionScope scope,
        string scheduleId,
        long generation)
    {
        return NativeWorldIdentity.Derive(
            "schedule-occurrence",
            scope.WorldId,
            scope.TimelineId,
            generation.ToString(CultureInfo.InvariantCulture),
            scope.TimelineEpoch.ToString(CultureInfo.InvariantCulture),
            scheduleId);
    }

    public static string ClaimToken(
        string occurrenceId,
        string claimantId,
        string operationId)
    {
        return NativeWorldIdentity.Derive(
            "schedule-claim",
            occurrenceId,
            claimantId,
            operationId);
    }

    public static string IntentDigest(WorldScheduleIntent intent)
    {
        return Digest(
            writer =>
            {
                writer.WriteStartObject();
                WriteScope(writer, intent.Scope);
                writer.WriteString(
                    "scheduleId",
                    intent.ScheduleId);
                WriteTime(writer, "dueAt", intent.DueAt);
                writer.WriteString(
                    "ownerEntityId",
                    intent.Owner.EntityId);
                writer.WriteString(
                    "ownerIncarnation",
                    intent.Owner.Incarnation.ToString(
                        CultureInfo.InvariantCulture));
                writer.WriteString(
                    "payloadSchemaId",
                    intent.PayloadSchemaId);
                writer.WriteString(
                    "payloadSchemaVersion",
                    intent.PayloadSchemaVersion);
                writer.WriteString(
                    "payloadSchemaDigest",
                    intent.PayloadSchemaDigest);
                writer.WriteString(
                    "payloadDigest",
                    intent.PayloadDigest);
                writer.WriteEndObject();
            });
    }

    public static string RecordDigest(WorldScheduleRecord record)
    {
        return Digest(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "intentDigest",
                    record.Intent.SemanticDigest);
                writer.WriteString(
                    "generation",
                    record.Generation.ToString(
                        CultureInfo.InvariantCulture));
                writer.WriteString(
                    "status",
                    ((int)record.Status).ToString(
                        CultureInfo.InvariantCulture));
                writer.WriteString(
                    "occurrenceId",
                    record.OccurrenceId);
                WriteClaim(writer, record.Claim);
                writer.WriteEndObject();
            });
    }

    public static string CommandFingerprint(
        WorldScheduleCommand command)
    {
        return Digest(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "kind",
                    ((int)command.Kind).ToString(
                        CultureInfo.InvariantCulture));
                writer.WriteString(
                    "operationId",
                    command.OperationId);
                WriteScope(writer, command.Scope);
                writer.WriteString(
                    "scheduleId",
                    command.ScheduleId);
                WriteOptionalInt64(
                    writer,
                    "expectedGeneration",
                    command.ExpectedGeneration);
                WriteOptionalString(
                    writer,
                    "createIntentDigest",
                    command.CreateIntent?.SemanticDigest);
                WriteOptionalTime(writer, "dueAt", command.DueAt);
                WriteOptionalTime(
                    writer,
                    "observedAt",
                    command.ObservedAt);
                WriteOptionalString(
                    writer,
                    "occurrenceId",
                    command.OccurrenceId);
                WriteOptionalString(
                    writer,
                    "claimantId",
                    command.ClaimantId);
                WriteOptionalString(
                    writer,
                    "claimToken",
                    command.ClaimToken);
                WriteOptionalString(
                    writer,
                    "replacementClaimantId",
                    command.ReplacementClaimantId);
                writer.WriteEndObject();
            });
    }

    public static string ReceiptId(
        WorldScheduleOperationReceipt receipt)
    {
        return Digest(
            writer =>
            {
                writer.WriteStartObject();
                WriteScope(writer, receipt.Scope);
                writer.WriteString(
                    "scheduleId",
                    receipt.ScheduleId);
                writer.WriteString(
                    "operationId",
                    receipt.OperationId);
                writer.WriteString(
                    "kind",
                    ((int)receipt.Kind).ToString(
                        CultureInfo.InvariantCulture));
                writer.WriteString(
                    "requestFingerprint",
                    receipt.RequestFingerprint);
                writer.WriteBoolean("applied", receipt.Applied);
                writer.WriteString(
                    "outcomeCode",
                    receipt.OutcomeCode);
                WriteOptionalInt64(
                    writer,
                    "resultingGeneration",
                    receipt.ResultingGeneration);
                WriteOptionalString(
                    writer,
                    "resultingStatus",
                    receipt.ResultingStatus.HasValue
                        ? ((int)receipt.ResultingStatus.Value)
                        .ToString(CultureInfo.InvariantCulture)
                        : null);
                WriteOptionalString(
                    writer,
                    "occurrenceId",
                    receipt.OccurrenceId);
                WriteClaim(writer, receipt.Claim);
                writer.WriteEndObject();
            });
    }

    private static string Digest(Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return CanonicalJsonDigest.ComputeSha256(
            document.RootElement);
    }

    private static void WriteScope(
        Utf8JsonWriter writer,
        WorldTransactionScope scope)
    {
        writer.WriteString("worldId", scope.WorldId);
        writer.WriteString("timelineId", scope.TimelineId);
        writer.WriteString(
            "timelineEpoch",
            scope.TimelineEpoch.ToString(
                CultureInfo.InvariantCulture));
    }

    private static void WriteTime(
        Utf8JsonWriter writer,
        string propertyName,
        GameTimePoint value)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("clockId", value.ClockId);
        writer.WriteString("timelineId", value.TimelineId);
        writer.WriteString(
            "epoch",
            value.Epoch.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "tick",
            value.Tick.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteOptionalTime(
        Utf8JsonWriter writer,
        string propertyName,
        GameTimePoint? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            WriteTime(writer, propertyName, value);
        }
    }

    private static void WriteClaim(
        Utf8JsonWriter writer,
        WorldScheduleClaim? claim)
    {
        writer.WritePropertyName("claim");
        if (claim is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("claimantId", claim.ClaimantId);
        writer.WriteString("claimToken", claim.ClaimToken);
        writer.WriteString("operationId", claim.OperationId);
        writer.WriteEndObject();
    }

    private static void WriteOptionalInt64(
        Utf8JsonWriter writer,
        string propertyName,
        long? value)
    {
        WriteOptionalString(
            writer,
            propertyName,
            value?.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }
}
