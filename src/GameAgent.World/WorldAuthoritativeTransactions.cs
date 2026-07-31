using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class WorldTransactionReasonCodes
{
    public const string Acquired = "world_transaction_acquired";
    public const string Applied = "world_transaction_applied";
    public const string EffectRejected = "world_effect_rejected";
    public const string EffectFailed = "world_effect_failed";
    public const string Cancelled = "world_transaction_cancelled";
    public const string Busy = "world_transaction_busy";
    public const string ReconciliationRequired =
        "world_transaction_reconciliation_required";
    public const string IdempotencyConflict =
        "world_transaction_idempotency_conflict";
    public const string StateNotFound = "world_state_not_found";
    public const string StaleCoordinate = "world_stale_coordinate";
    public const string StaleVersion = "world_stale_version";
    public const string StaleCatalog = "world_stale_catalog";
    public const string StaleIncarnation = "world_stale_incarnation";
    public const string EventAlreadyCommitted =
        "world_event_already_committed";
    public const string InvalidHistory = "world_transaction_invalid_history";
    public const string LeaseLost = "world_transaction_lease_lost";
}

/// <summary>
/// Identifies one current timeline independently of its mutable versions.
/// </summary>
public sealed class WorldTimelineAddress
{
    public WorldTimelineAddress(string worldId, string timelineId)
    {
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    internal string StableKey =>
        WorldValidation.ComposeStableKey(WorldId, TimelineId);
}

/// <summary>
/// Immutable isolation scope for idempotency and reconciliation. Operation
/// and command identifiers may be reused only in a different scope.
/// </summary>
public sealed class WorldTransactionScope
{
    public WorldTransactionScope(
        string worldId,
        string timelineId,
        long timelineEpoch)
    {
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        TimelineEpoch = timelineEpoch;
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    internal string StableKey =>
        WorldValidation.ComposeStableKey(
            WorldId,
            TimelineId,
            TimelineEpoch.ToString(
                CultureInfo.InvariantCulture));

    public bool IsSameAs(WorldTransactionScope other)
    {
        return other is not null
               && string.Equals(
                   WorldId,
                   other.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   TimelineId,
                   other.TimelineId,
                   StringComparison.Ordinal)
               && TimelineEpoch == other.TimelineEpoch;
    }
}

/// <summary>
/// Exact optimistic coordinate for authoritative world work.
/// </summary>
public sealed class WorldAuthoritativeCoordinate
{
    public WorldAuthoritativeCoordinate(
        string worldId,
        string timelineId,
        long timelineEpoch,
        long saveRevision,
        long stateVersion,
        string catalogDigest)
    {
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        if (saveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        if (stateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateVersion));
        }

        TimelineEpoch = timelineEpoch;
        SaveRevision = saveRevision;
        StateVersion = stateVersion;
        CatalogDigest = WorldValidation.Required(
            catalogDigest,
            nameof(catalogDigest),
            256);
        if (!CanonicalJsonDigest.IsSha256(CatalogDigest))
        {
            throw new ArgumentException(
                "Catalog digest must be a lowercase SHA-256 digest.",
                nameof(catalogDigest));
        }
    }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public long SaveRevision { get; }

    public long StateVersion { get; }

    public string CatalogDigest { get; }

    public WorldTimelineAddress Address =>
        new(WorldId, TimelineId);

    public WorldTransactionScope Scope =>
        new(WorldId, TimelineId, TimelineEpoch);

    public bool IsSameTimelineAs(WorldAuthoritativeCoordinate other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        return string.Equals(
                   WorldId,
                   other.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   TimelineId,
                   other.TimelineId,
                   StringComparison.Ordinal)
               && TimelineEpoch == other.TimelineEpoch;
    }

    public bool IsExactMatch(WorldAuthoritativeCoordinate other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        return IsSameTimelineAs(other)
               && SaveRevision == other.SaveRevision
               && StateVersion == other.StateVersion
               && string.Equals(
                   CatalogDigest,
                   other.CatalogDigest,
                   StringComparison.Ordinal);
    }

    public WorldAuthoritativeCoordinate Advance(bool stateChanged)
    {
        if (SaveRevision == long.MaxValue
            || (stateChanged && StateVersion == long.MaxValue))
        {
            throw new InvalidOperationException(
                "The authoritative coordinate cannot advance further.");
        }

        return new WorldAuthoritativeCoordinate(
            WorldId,
            TimelineId,
            TimelineEpoch,
            SaveRevision + 1,
            stateChanged ? StateVersion + 1 : StateVersion,
            CatalogDigest);
    }
}

public sealed class WorldEntityIncarnationExpectation
{
    public WorldEntityIncarnationExpectation(
        string entityId,
        long incarnation)
    {
        EntityId = WorldValidation.Required(
            entityId,
            nameof(entityId));
        if (incarnation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        }

        Incarnation = incarnation;
    }

    public string EntityId { get; }

    public long Incarnation { get; }
}

/// <summary>
/// One exact entity lifetime that the authoritative world has issued.
/// Issuance is permanent for the lifetime of a timeline, including after
/// the entity is removed from the current-authority map.
/// </summary>
public sealed class WorldIssuedEntityIncarnation
{
    public WorldIssuedEntityIncarnation(
        string entityId,
        long incarnation)
    {
        EntityId = WorldValidation.Required(
            entityId,
            nameof(entityId));
        if (incarnation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        }

        Incarnation = incarnation;
    }

    public string EntityId { get; }

    public long Incarnation { get; }
}

/// <summary>
/// Immutable state visible to planners and transaction-local effect handlers.
/// The JSON root has no framework-defined gameplay fields.
/// </summary>
public sealed class WorldAuthoritativeStateSnapshot
{
    public const int MaximumIssuedEntityCount = 65_536;

    public const int MaximumIssuedIncarnationCount = 65_536;

    private static readonly JsonValueLimits StateLimits = new(
        maxUtf8Bytes: 8 * 1024 * 1024,
        maxDepth: 64,
        maxNodes: 250_000,
        maxStringUtf8Bytes: 1024 * 1024,
        maxContainerItems: 100_000);

    public WorldAuthoritativeStateSnapshot(
        WorldAuthoritativeCoordinate coordinate,
        JsonElement state,
        IReadOnlyDictionary<string, long>? entityIncarnations = null)
        : this(
            coordinate,
            state,
            entityIncarnations,
            issuedEntityIncarnations: null)
    {
    }

    public WorldAuthoritativeStateSnapshot(
        WorldAuthoritativeCoordinate coordinate,
        JsonElement state,
        IReadOnlyDictionary<string, long>? entityIncarnations,
        IEnumerable<WorldIssuedEntityIncarnation>?
            issuedEntityIncarnations)
    {
        Coordinate = coordinate
                     ?? throw new ArgumentNullException(nameof(coordinate));
        ValidateState(state, nameof(state));
        State = state.Clone();
        StateDigest = WorldStateJson.ComputeDigest(State);
        EntityIncarnations = CopyIncarnations(entityIncarnations);
        var issued = CopyIssuedIncarnations(
            issuedEntityIncarnations,
            EntityIncarnations);
        IssuedEntityIncarnations = issued.Records;
        _issuedIncarnations = issued.Lookup;
    }

    private readonly IReadOnlyDictionary<string, HashSet<long>>
        _issuedIncarnations;

    public WorldAuthoritativeCoordinate Coordinate { get; }

    public JsonElement State { get; }

    public string StateDigest { get; }

    public IReadOnlyDictionary<string, long> EntityIncarnations { get; }

    public IReadOnlyList<WorldIssuedEntityIncarnation>
        IssuedEntityIncarnations
    { get; }

    public bool TryGetIncarnation(string entityId, out long incarnation)
    {
        return EntityIncarnations.TryGetValue(
            WorldValidation.Required(entityId, nameof(entityId)),
            out incarnation);
    }

    public bool WasIncarnationIssued(
        string entityId,
        long incarnation)
    {
        var normalized = WorldValidation.Required(
            entityId,
            nameof(entityId));
        if (incarnation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        }

        return _issuedIncarnations.TryGetValue(
                   normalized,
                   out var incarnations)
               && incarnations.Contains(incarnation);
    }

    internal bool TryGetLatestIssuedIncarnation(
        string entityId,
        out long incarnation)
    {
        if (_issuedIncarnations.TryGetValue(
                entityId,
                out var incarnations))
        {
            incarnation = incarnations.Max();
            return true;
        }

        incarnation = default;
        return false;
    }

    internal static void ValidateState(JsonElement state, string name)
    {
        JsonValueInspector.ValidateAndMeasure(state, StateLimits, name);
        WorldStateJson.RejectNumbers(state, name);
    }

    private static IReadOnlyDictionary<string, long> CopyIncarnations(
        IReadOnlyDictionary<string, long>? values)
    {
        var copy = new SortedDictionary<string, long>(
            StringComparer.Ordinal);
        if (values is not null)
        {
            var bounded = WorldValidation.MaterializeBounded(
                values,
                WorldValidation.MaximumParticipants,
                () => new ArgumentException(
                    "The incarnation collection exceeds its item limit.",
                    nameof(values)));
            foreach (var pair in bounded)
            {
                var key = WorldValidation.Required(
                    pair.Key,
                    nameof(values));
                if (pair.Value < 0)
                {
                    throw new ArgumentException(
                        "Entity incarnations cannot be negative.",
                        nameof(values));
                }

                if (!copy.TryAdd(key, pair.Value))
                {
                    throw new ArgumentException(
                        "Entity identifiers must be unique.",
                        nameof(values));
                }
            }
        }

        return new ReadOnlyDictionary<string, long>(
            new Dictionary<string, long>(copy, StringComparer.Ordinal));
    }

    private static IssuedIncarnationCopy CopyIssuedIncarnations(
        IEnumerable<WorldIssuedEntityIncarnation>? values,
        IReadOnlyDictionary<string, long> current)
    {
        IEnumerable<WorldIssuedEntityIncarnation> source;
        if (values is null)
        {
            source = current.Select(
                pair => new WorldIssuedEntityIncarnation(
                    pair.Key,
                    pair.Value));
        }
        else
        {
            source = values;
        }

        var bounded = WorldValidation.MaterializeBounded(
            source,
            MaximumIssuedIncarnationCount,
            () => new ArgumentException(
                "The issued-incarnation ledger exceeds its item limit.",
                nameof(values)));
        var lookup = new Dictionary<string, HashSet<long>>(
            StringComparer.Ordinal);
        foreach (var item in bounded)
        {
            if (item is null)
            {
                throw new ArgumentException(
                    "The issued-incarnation ledger cannot contain null records.",
                    nameof(values));
            }

            var entityId = WorldValidation.Required(
                item.EntityId,
                nameof(values));
            if (!lookup.TryGetValue(entityId, out var incarnations))
            {
                if (lookup.Count >= MaximumIssuedEntityCount)
                {
                    throw new ArgumentException(
                        "The issued-incarnation ledger exceeds its entity limit.",
                        nameof(values));
                }

                incarnations = new HashSet<long>();
                lookup.Add(entityId, incarnations);
            }

            if (!incarnations.Add(item.Incarnation))
            {
                throw new ArgumentException(
                    "Issued entity incarnations must be unique.",
                    nameof(values));
            }
        }

        foreach (var pair in current)
        {
            if (!lookup.TryGetValue(pair.Key, out var incarnations)
                || !incarnations.Contains(pair.Value)
                || incarnations.Max() != pair.Value)
            {
                throw new ArgumentException(
                    "Every current entity incarnation must be the latest exact lifetime in the issued-incarnation ledger.",
                    nameof(values));
            }
        }

        var records = lookup
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(
                pair => pair.Value
                    .OrderBy(value => value)
                    .Select(
                        value => new WorldIssuedEntityIncarnation(
                            pair.Key,
                            value)))
            .ToArray();
        var readOnlyLookup =
            new Dictionary<string, HashSet<long>>(
                StringComparer.Ordinal);
        foreach (var pair in lookup)
        {
            readOnlyLookup.Add(
                pair.Key,
                new HashSet<long>(pair.Value));
        }

        return new IssuedIncarnationCopy(
            new ReadOnlyCollection<WorldIssuedEntityIncarnation>(
                records),
            new ReadOnlyDictionary<string, HashSet<long>>(
                readOnlyLookup));
    }

    private sealed class IssuedIncarnationCopy
    {
        public IssuedIncarnationCopy(
            IReadOnlyList<WorldIssuedEntityIncarnation> records,
            IReadOnlyDictionary<string, HashSet<long>> lookup)
        {
            Records = records;
            Lookup = lookup;
        }

        public IReadOnlyList<WorldIssuedEntityIncarnation> Records { get; }

        public IReadOnlyDictionary<string, HashSet<long>> Lookup
        { get; }
    }
}

/// <summary>
/// Copy-on-write view owned by one authoritative transaction. Mutations here
/// are provisional until the store accepts one atomic commit.
/// </summary>
public interface IWorldStateDraft
{
    WorldAuthoritativeStateSnapshot Source { get; }

    JsonElement State { get; }

    string StateDigest { get; }

    IReadOnlyDictionary<string, long> EntityIncarnations { get; }

    bool HasChanges { get; }

    void ReplaceState(JsonElement state);

    bool TryGetIncarnation(string entityId, out long incarnation);

    void SetIncarnation(string entityId, long incarnation);

    void RemoveIncarnation(string entityId);
}

internal interface IWorldIssuedIncarnationDraft
{
    IReadOnlyList<WorldIssuedEntityIncarnation>
        IssuedEntityIncarnations
    { get; }
}

public enum WorldCommandReceiptStatus
{
    Applied = 0,
    Rejected = 1,
    Cancelled = 2
}

public sealed class WorldEffectReceipt
{
    public WorldEffectReceipt(
        bool applied,
        string outcomeCode,
        JsonElement? typedResult = null)
    {
        Applied = applied;
        OutcomeCode = WorldValidation.Required(
            outcomeCode,
            nameof(outcomeCode),
            96);
        if (typedResult.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                typedResult.Value,
                new JsonValueLimits(
                    maxUtf8Bytes: 65_536,
                    maxDepth: 24,
                    maxNodes: 4_096,
                    maxStringUtf8Bytes: 16_384,
                    maxContainerItems: 2_048),
                nameof(typedResult));
            TypedResult = typedResult.Value.Clone();
        }
    }

    public bool Applied { get; }

    public string OutcomeCode { get; }

    public JsonElement? TypedResult { get; }
}

/// <summary>
/// Durable terminal result of one idempotent world command.
/// </summary>
public sealed class WorldCommandReceipt
{
    public WorldCommandReceipt(
        WorldTransactionRequest request,
        WorldCommandReceiptStatus status,
        string outcomeCode,
        WorldAuthoritativeCoordinate? resultingCoordinate,
        string? resultingStateDigest,
        WorldEffectReceipt? effect,
        string? eventInstanceId)
    {
        Request = request
                  ?? throw new ArgumentNullException(nameof(request));
        if (!Enum.IsDefined(typeof(WorldCommandReceiptStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        OutcomeCode = WorldValidation.Required(
            outcomeCode,
            nameof(outcomeCode),
            96);
        ResultingCoordinate = resultingCoordinate;
        ResultingStateDigest = resultingStateDigest;
        if (resultingStateDigest is not null
            && !CanonicalJsonDigest.IsSha256(resultingStateDigest))
        {
            throw new ArgumentException(
                "State digest must be a lowercase SHA-256 digest.",
                nameof(resultingStateDigest));
        }

        if (resultingCoordinate is not null
            && !request.ExpectedCoordinate.IsSameTimelineAs(
                resultingCoordinate))
        {
            throw new ArgumentException(
                "A receipt cannot cross a world, timeline, or epoch.",
                nameof(resultingCoordinate));
        }

        Effect = effect;
        EventInstanceId = WorldValidation.Optional(
            eventInstanceId,
            nameof(eventInstanceId));
        ValidateTerminalShape();
        ReceiptId = WorldTransactionIdentity.ComputeReceiptId(this);
    }

    public WorldTransactionRequest Request { get; }

    public string OperationId => Request.OperationId;

    public string CommandId => Request.CommandId;

    public string RequestFingerprint => Request.RequestFingerprint;

    public WorldCommandReceiptStatus Status { get; }

    public string OutcomeCode { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate =>
        Request.ExpectedCoordinate;

    public WorldAuthoritativeCoordinate? ResultingCoordinate { get; }

    public string? ResultingStateDigest { get; }

    public WorldEffectReceipt? Effect { get; }

    public string? EventInstanceId { get; }

    public string ReceiptId { get; }

    private void ValidateTerminalShape()
    {
        if (Status == WorldCommandReceiptStatus.Applied)
        {
            if (ResultingCoordinate is null
                || ResultingStateDigest is null
                || Effect is null
                || !Effect.Applied
                || Request.EventOccurrence is null
                || !string.Equals(
                    EventInstanceId,
                    Request.EventOccurrence.InstanceId,
                    StringComparison.Ordinal)
                || !Request.ExpectedCoordinate.IsSameTimelineAs(
                    ResultingCoordinate)
                || !string.Equals(
                    Request.ExpectedCoordinate.CatalogDigest,
                    ResultingCoordinate.CatalogDigest,
                    StringComparison.Ordinal)
                || Request.ExpectedCoordinate.SaveRevision == long.MaxValue
                || ResultingCoordinate.SaveRevision
                != Request.ExpectedCoordinate.SaveRevision + 1
                || (ResultingCoordinate.StateVersion
                    != Request.ExpectedCoordinate.StateVersion
                    && (Request.ExpectedCoordinate.StateVersion
                        == long.MaxValue
                        || ResultingCoordinate.StateVersion
                        != Request.ExpectedCoordinate.StateVersion + 1)))
            {
                throw new ArgumentException(
                    "An applied receipt has an inconsistent terminal shape.");
            }

            return;
        }

        if (EventInstanceId is not null
            || (Effect is not null && Effect.Applied)
            || (Status == WorldCommandReceiptStatus.Cancelled
                && Effect is not null)
            || (ResultingCoordinate is null)
            != (ResultingStateDigest is null))
        {
            throw new ArgumentException(
                "A non-applied receipt has an inconsistent terminal shape.");
        }
    }
}

/// <summary>
/// Immutable admission request. BeginAsync must durably reserve the operation
/// before it returns an acquired transaction.
/// </summary>
public sealed class WorldTransactionRequest
{
    public WorldTransactionRequest(
        string operationId,
        string commandId,
        string commandPayloadDigest,
        WorldAuthoritativeCoordinate expectedCoordinate,
        IReadOnlyList<WorldEntityIncarnationExpectation>?
            expectedIncarnations = null,
        WorldEventHistoryRecord? eventOccurrence = null)
    {
        OperationId = WorldValidation.Required(
            operationId,
            nameof(operationId));
        CommandId = WorldValidation.Required(
            commandId,
            nameof(commandId));
        CommandPayloadDigest = WorldValidation.Required(
            commandPayloadDigest,
            nameof(commandPayloadDigest),
            256);
        if (!CanonicalJsonDigest.IsSha256(CommandPayloadDigest))
        {
            throw new ArgumentException(
                "Command payload digest must be a lowercase SHA-256 digest.",
                nameof(commandPayloadDigest));
        }
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
        ExpectedIncarnations = CopyExpectations(expectedIncarnations);
        EventOccurrence = eventOccurrence;
        if (eventOccurrence is not null
            && (!string.Equals(
                    eventOccurrence.Definition.WorldId,
                    expectedCoordinate.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    eventOccurrence.Definition.TimelineId,
                    expectedCoordinate.TimelineId,
                    StringComparison.Ordinal)
                || eventOccurrence.Definition.TimelineEpoch
                != expectedCoordinate.TimelineEpoch))
        {
            throw new ArgumentException(
                "The event occurrence must use the expected coordinate.",
                nameof(eventOccurrence));
        }

        RequestFingerprint =
            WorldTransactionIdentity.ComputeRequestFingerprint(this);
    }

    public string OperationId { get; }

    public string CommandId { get; }

    public string CommandPayloadDigest { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    public IReadOnlyList<WorldEntityIncarnationExpectation>
        ExpectedIncarnations
    { get; }

    public WorldEventHistoryRecord? EventOccurrence { get; }

    public string RequestFingerprint { get; }

    internal string ScopedOperationKey =>
        WorldValidation.ComposeStableKey(
            ExpectedCoordinate.Scope.StableKey,
            OperationId);

    internal string ScopedCommandKey =>
        WorldValidation.ComposeStableKey(
            ExpectedCoordinate.Scope.StableKey,
            CommandId);

    private static IReadOnlyList<WorldEntityIncarnationExpectation>
        CopyExpectations(
            IReadOnlyList<WorldEntityIncarnationExpectation>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Array.Empty<WorldEntityIncarnationExpectation>();
        }

        if (values.Count > WorldValidation.MaximumParticipants)
        {
            throw new ArgumentException(
                "The expectation collection exceeds its item limit.",
                nameof(values));
        }

        var ordered = WorldValidation.MaterializeBounded(
                values,
                WorldValidation.MaximumParticipants,
                nameof(values))
            .Select(
                item => item
                        ?? throw new ArgumentException(
                            "Expectations cannot contain null entries.",
                            nameof(values)))
            .OrderBy(item => item.EntityId, StringComparer.Ordinal)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(
                    ordered[index - 1].EntityId,
                    ordered[index].EntityId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Expected entity identifiers must be unique.",
                    nameof(values));
            }
        }

        return new ReadOnlyCollection<WorldEntityIncarnationExpectation>(
            ordered);
    }
}

public enum WorldTransactionBeginStatus
{
    Acquired = 0,
    TerminalReceipt = 1,
    Busy = 2,
    ReconciliationRequired = 3,
    IdempotencyConflict = 4
}

public sealed class WorldTransactionBeginResult
{
    private WorldTransactionBeginResult(
        WorldTransactionBeginStatus status,
        string reasonCode,
        IWorldAuthoritativeTransaction? transaction,
        WorldCommandReceipt? receipt)
    {
        Status = status;
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        Transaction = transaction;
        Receipt = receipt;
    }

    public WorldTransactionBeginStatus Status { get; }

    public string ReasonCode { get; }

    public IWorldAuthoritativeTransaction? Transaction { get; }

    public WorldCommandReceipt? Receipt { get; }

    public static WorldTransactionBeginResult Acquired(
        IWorldAuthoritativeTransaction transaction)
    {
        return new WorldTransactionBeginResult(
            WorldTransactionBeginStatus.Acquired,
            WorldTransactionReasonCodes.Acquired,
            transaction
            ?? throw new ArgumentNullException(nameof(transaction)),
            null);
    }

    public static WorldTransactionBeginResult Terminal(
        WorldCommandReceipt receipt)
    {
        return new WorldTransactionBeginResult(
            WorldTransactionBeginStatus.TerminalReceipt,
            receipt?.OutcomeCode
            ?? throw new ArgumentNullException(nameof(receipt)),
            null,
            receipt);
    }

    public static WorldTransactionBeginResult NonTerminal(
        WorldTransactionBeginStatus status,
        string reasonCode)
    {
        if (status is WorldTransactionBeginStatus.Acquired
            or WorldTransactionBeginStatus.TerminalReceipt)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new WorldTransactionBeginResult(
            status,
            reasonCode,
            null,
            null);
    }
}

public enum WorldTransactionCommitStatus
{
    Committed = 0,
    LeaseLost = 1
}

public sealed class WorldTransactionCommitResult
{
    private WorldTransactionCommitResult(
        WorldTransactionCommitStatus status,
        string reasonCode,
        WorldCommandReceipt? receipt)
    {
        Status = status;
        ReasonCode = reasonCode;
        Receipt = receipt;
    }

    public WorldTransactionCommitStatus Status { get; }

    public string ReasonCode { get; }

    public WorldCommandReceipt? Receipt { get; }

    public static WorldTransactionCommitResult Committed(
        WorldCommandReceipt receipt)
    {
        return new WorldTransactionCommitResult(
            WorldTransactionCommitStatus.Committed,
            receipt?.OutcomeCode
            ?? throw new ArgumentNullException(nameof(receipt)),
            receipt);
    }

    public static WorldTransactionCommitResult LeaseLost()
    {
        return new WorldTransactionCommitResult(
            WorldTransactionCommitStatus.LeaseLost,
            WorldTransactionReasonCodes.LeaseLost,
            null);
    }
}

/// <summary>
/// Store-owned capability for one single-writer transaction.
/// </summary>
public interface IWorldAuthoritativeTransaction : IAsyncDisposable
{
    WorldTransactionRequest Request { get; }

    WorldAuthoritativeStateSnapshot Source { get; }

    IWorldStateDraft Draft { get; }

    ValueTask<WorldTransactionCommitResult> CommitEventAsync(
        WorldEffectReceipt effect,
        CancellationToken cancellationToken);

    ValueTask<WorldTransactionCommitResult> CompleteWithoutMutationAsync(
        WorldCommandReceiptStatus status,
        string outcomeCode,
        WorldEffectReceipt? effect,
        CancellationToken cancellationToken);
}

public enum WorldTransactionReconciliationStatus
{
    TerminalReceipt = 0,
    Pending = 1,
    NotFound = 2,
    IdempotencyConflict = 3
}

public sealed class WorldTransactionReconciliationResult
{
    private WorldTransactionReconciliationResult(
        WorldTransactionReconciliationStatus status,
        string reasonCode,
        WorldCommandReceipt? receipt)
    {
        Status = status;
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        Receipt = receipt;
    }

    public WorldTransactionReconciliationStatus Status { get; }

    public string ReasonCode { get; }

    public WorldCommandReceipt? Receipt { get; }

    public static WorldTransactionReconciliationResult Terminal(
        WorldCommandReceipt receipt)
    {
        return new WorldTransactionReconciliationResult(
            WorldTransactionReconciliationStatus.TerminalReceipt,
            receipt?.OutcomeCode
            ?? throw new ArgumentNullException(nameof(receipt)),
            receipt);
    }

    public static WorldTransactionReconciliationResult NonTerminal(
        WorldTransactionReconciliationStatus status,
        string reasonCode)
    {
        if (status == WorldTransactionReconciliationStatus.TerminalReceipt)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new WorldTransactionReconciliationResult(
            status,
            reasonCode,
            null);
    }
}

public enum WorldTransactionInspectionStatus
{
    NotFound = 0,
    Pending = 1,
    TerminalReceipt = 2
}

/// <summary>
/// Immutable recovery view for a scoped operation. Inspection deliberately
/// returns the durably stored request so a higher-level executor can validate
/// its exact semantic identity before trusting a pending or terminal record.
/// </summary>
public sealed class WorldTransactionInspectionResult
{
    private WorldTransactionInspectionResult(
        WorldTransactionInspectionStatus status,
        WorldTransactionRequest? request,
        WorldCommandReceipt? receipt)
    {
        Status = status;
        Request = request;
        Receipt = receipt;
    }

    public WorldTransactionInspectionStatus Status { get; }

    public WorldTransactionRequest? Request { get; }

    public WorldCommandReceipt? Receipt { get; }

    public static WorldTransactionInspectionResult NotFound()
    {
        return new WorldTransactionInspectionResult(
            WorldTransactionInspectionStatus.NotFound,
            null,
            null);
    }

    public static WorldTransactionInspectionResult Pending(
        WorldTransactionRequest request)
    {
        return new WorldTransactionInspectionResult(
            WorldTransactionInspectionStatus.Pending,
            request ?? throw new ArgumentNullException(nameof(request)),
            null);
    }

    public static WorldTransactionInspectionResult Terminal(
        WorldCommandReceipt receipt)
    {
        var value = receipt
                    ?? throw new ArgumentNullException(nameof(receipt));
        return new WorldTransactionInspectionResult(
            WorldTransactionInspectionStatus.TerminalReceipt,
            value.Request,
            value);
    }
}

/// <summary>
/// Authoritative persistence boundary. A production implementation normally
/// maps Begin, commit, history, and receipt records to one durable database
/// transaction. Begin must persist pending ownership before returning.
/// </summary>
public interface IWorldAuthoritativeTransactionStore
{
    ValueTask<WorldAuthoritativeStateSnapshot?> ReadAsync(
        WorldTimelineAddress address,
        CancellationToken cancellationToken);

    ValueTask<WorldTransactionBeginResult> BeginAsync(
        WorldTransactionRequest request,
        CancellationToken cancellationToken);

    ValueTask<WorldTransactionInspectionResult> InspectAsync(
        WorldTransactionScope scope,
        string operationId,
        CancellationToken cancellationToken);

    ValueTask<WorldTransactionReconciliationResult> ReconcileAsync(
        WorldTransactionScope scope,
        string operationId,
        string requestFingerprint,
        CancellationToken cancellationToken);

    ValueTask<WorldTransactionReconciliationResult> CancelPendingAsync(
        WorldTransactionScope scope,
        string operationId,
        string requestFingerprint,
        string outcomeCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// Process-local conformance implementation. It provides atomic behavior and
/// crash/retry semantics within one process, but is not durable storage.
/// </summary>
public sealed class InMemoryWorldAuthoritativeTransactionStore
    : IWorldAuthoritativeTransactionStore,
      IWorldEventHistory,
      IWorldScheduleStore,
      IWorldAuthoritativeStoreCaptureSource,
      IWorldAuthoritativeReceiptSource
{
    private readonly object _sync = new();

    private readonly WorldScheduleStoreOptions _scheduleOptions;

    private readonly Dictionary<string, StateCell> _states =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, PendingOperation> _pendingByOperation =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, WorldCommandReceipt>
        _receiptsByOperation = new(StringComparer.Ordinal);

    private readonly Dictionary<string, OperationIdentity> _commands =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, WorldEventHistoryRecord> _history =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, DefinitionState> _definitionHistory =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, WorldScheduleRecord> _schedules =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, WorldScheduleOperationReceipt>
        _scheduleOperations = new(StringComparer.Ordinal);

    public InMemoryWorldAuthoritativeTransactionStore(
        IEnumerable<WorldAuthoritativeStateSnapshot> initialStates,
        WorldScheduleStoreOptions? scheduleOptions = null)
    {
        if (initialStates is null)
        {
            throw new ArgumentNullException(nameof(initialStates));
        }

        _scheduleOptions =
            scheduleOptions ?? new WorldScheduleStoreOptions();
        var boundedStates = WorldValidation.MaterializeBounded(
            initialStates,
            WorldValidation.MaximumStates,
            nameof(initialStates));
        foreach (var state in boundedStates)
        {
            if (state is null)
            {
                throw new ArgumentException(
                    "Initial states cannot contain null entries.",
                    nameof(initialStates));
            }

            var key = state.Coordinate.Address.StableKey;
            if (!_states.TryAdd(key, new StateCell(state)))
            {
                throw new ArgumentException(
                    "Initial states must be unique by world and timeline.",
                    nameof(initialStates));
            }
        }
    }

    public InMemoryWorldAuthoritativeTransactionStore(
        WorldAuthoritativeStateSnapshot initialState,
        WorldScheduleStoreOptions? scheduleOptions = null)
        : this(
            new[]
            {
                initialState
                ?? throw new ArgumentNullException(nameof(initialState))
            },
            scheduleOptions)
    {
    }

    internal InMemoryWorldAuthoritativeTransactionStore(
        WorldAuthoritativeStoreCapture capture,
        WorldScheduleStoreOptions? scheduleOptions = null)
        : this(
            (capture
             ?? throw new ArgumentNullException(nameof(capture))).Snapshot,
            scheduleOptions)
    {
        foreach (var receipt in capture.Receipts)
        {
            _receiptsByOperation.Add(
                receipt.Request.ScopedOperationKey,
                receipt);
            _commands.Add(
                receipt.Request.ScopedCommandKey,
                new OperationIdentity(
                    receipt.OperationId,
                    receipt.RequestFingerprint));
        }

        foreach (var record in capture.History)
        {
            var update = PrepareHistoryAppend(record);
            AppendHistory(record, update);
        }

        foreach (var schedule in capture.Schedules)
        {
            _schedules.Add(schedule.StableKey, schedule);
        }

        foreach (var operation in capture.ScheduleOperations)
        {
            _scheduleOperations.Add(
                operation.ScopedOperationKey,
                operation);
        }

        WorldScheduleStoreLogic.EnsureCapacity(
            _schedules.Values,
            _scheduleOperations.Count,
            _scheduleOptions);
    }

    public ValueTask<WorldAuthoritativeStateSnapshot?> ReadAsync(
        WorldTimelineAddress address,
        CancellationToken cancellationToken)
    {
        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _states.TryGetValue(address.StableKey, out var cell);
            return new ValueTask<WorldAuthoritativeStateSnapshot?>(
                cell?.Snapshot);
        }
    }

    ValueTask<WorldCommandReceipt?>
        IWorldAuthoritativeReceiptSource.ReadReceiptAsync(
            WorldTimelineAddress address,
            long timelineEpoch,
            string receiptId,
            int maximumTransactionRecords,
            CancellationToken cancellationToken)
    {
        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        var normalizedReceiptId = WorldValidation.Required(
            receiptId,
            nameof(receiptId),
            128);
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }
        if (maximumTransactionRecords < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactionRecords));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_receiptsByOperation.Count > maximumTransactionRecords)
            {
                return new ValueTask<WorldCommandReceipt?>(
                    result: null);
            }

            WorldCommandReceipt? match = null;
            foreach (var receipt in _receiptsByOperation.Values)
            {
                if (!string.Equals(
                        receipt.ReceiptId,
                        normalizedReceiptId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        receipt.ExpectedCoordinate.Address.StableKey,
                        address.StableKey,
                        StringComparison.Ordinal)
                    || receipt.ExpectedCoordinate.TimelineEpoch
                    != timelineEpoch)
                {
                    continue;
                }

                if (match is not null)
                {
                    return new ValueTask<WorldCommandReceipt?>(
                        result: null);
                }

                match = receipt;
            }

            return new ValueTask<WorldCommandReceipt?>(match);
        }
    }

    public ValueTask<WorldScheduleMutationResult> ExecuteAsync(
        WorldScheduleCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldScheduleMutationResult>(
                WorldScheduleStoreLogic.Execute(
                    _schedules,
                    _scheduleOperations,
                    _states.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Snapshot,
                        StringComparer.Ordinal),
                    command,
                    _scheduleOptions));
        }
    }

    public ValueTask<WorldScheduleRecord?> FindAsync(
        WorldTransactionScope scope,
        string scheduleId,
        CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var normalized = WorldValidation.Required(
            scheduleId,
            nameof(scheduleId),
            192);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _schedules.TryGetValue(
                WorldValidation.ComposeStableKey(
                    scope.StableKey,
                    normalized),
                out var schedule);
            return new ValueTask<WorldScheduleRecord?>(schedule);
        }
    }

    public ValueTask<WorldScheduleDuePage> QueryDueAsync(
        WorldScheduleDueQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return new ValueTask<WorldScheduleDuePage>(
                WorldScheduleStoreLogic.QueryDue(
                    _schedules.Values,
                    query,
                    cancellationToken));
        }
    }

    ValueTask<WorldAuthoritativeStoreCapture>
        IWorldAuthoritativeStoreCaptureSource.CaptureSettledAsync(
            WorldTimelineAddress address,
            long timelineEpoch,
            int maximumTransactionRecords,
            int maximumHistoryRecords,
            int maximumScheduleRecords,
            int maximumScheduleOperations,
            CancellationToken cancellationToken)
    {
        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_states.TryGetValue(
                    address.StableKey,
                    out var cell)
                || cell.Snapshot.Coordinate.TimelineEpoch
                != timelineEpoch)
            {
                throw new NativeWorldSaveBridgeException(
                    NativeWorldSaveBridgeReasonCodes.BindingMismatch,
                    "The requested authoritative timeline does not exist.");
            }

            if (_pendingByOperation.Count
                    + _receiptsByOperation.Count
                > maximumTransactionRecords
                || _history.Count > maximumHistoryRecords
                || _schedules.Count > maximumScheduleRecords
                || _scheduleOperations.Count
                > maximumScheduleOperations)
            {
                throw new NativeWorldSaveBridgeException(
                    NativeWorldSaveBridgeReasonCodes.CapacityExceeded,
                    "The authoritative store exceeds the capture scan limit.");
            }

            var coordinate = cell.Snapshot.Coordinate;
            var receipts = new List<WorldCommandReceipt>();
            foreach (var pending in _pendingByOperation.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SameScope(
                        pending.Request.ExpectedCoordinate,
                        coordinate))
                {
                    throw new NativeWorldSaveBridgeException(
                        NativeWorldSaveBridgeReasonCodes.PendingTransactions,
                        "Settled capture rejects pending authoritative work.");
                }
            }

            foreach (var receipt in _receiptsByOperation.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SameScope(
                        receipt.Request.ExpectedCoordinate,
                        coordinate))
                {
                    receipts.Add(receipt);
                }
            }

            var history = new List<WorldEventHistoryRecord>();
            foreach (var record in _history.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(
                        record.Definition.WorldId,
                        coordinate.WorldId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        record.Definition.TimelineId,
                        coordinate.TimelineId,
                        StringComparison.Ordinal)
                    && record.Definition.TimelineEpoch
                    == coordinate.TimelineEpoch)
                {
                    history.Add(record);
                }
            }

            var schedules = new List<WorldScheduleRecord>();
            foreach (var schedule in _schedules.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (schedule.Scope.IsSameAs(coordinate.Scope))
                {
                    schedules.Add(schedule);
                }
            }

            var scheduleOperations =
                new List<WorldScheduleOperationReceipt>();
            foreach (var operation in _scheduleOperations.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation.Scope.IsSameAs(coordinate.Scope))
                {
                    scheduleOperations.Add(operation);
                }
            }

            return new ValueTask<WorldAuthoritativeStoreCapture>(
                new WorldAuthoritativeStoreCapture(
                    cell.Snapshot,
                    receipts,
                    history,
                    maximumTransactionRecords,
                    maximumHistoryRecords,
                    schedules,
                    scheduleOperations,
                    maximumScheduleRecords,
                    maximumScheduleOperations));
        }
    }

    public ValueTask<WorldTransactionBeginResult> BeginAsync(
        WorldTransactionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_receiptsByOperation.TryGetValue(
                    request.ScopedOperationKey,
                    out var existingReceipt))
            {
                return new ValueTask<WorldTransactionBeginResult>(
                    IsSameRequest(existingReceipt.Request, request)
                        ? WorldTransactionBeginResult.Terminal(existingReceipt)
                        : Conflict());
            }

            if (_pendingByOperation.TryGetValue(
                    request.ScopedOperationKey,
                    out var existingPending))
            {
                return new ValueTask<WorldTransactionBeginResult>(
                    IsSameRequest(existingPending.Request, request)
                        ? WorldTransactionBeginResult.NonTerminal(
                            WorldTransactionBeginStatus
                                .ReconciliationRequired,
                            WorldTransactionReasonCodes
                                .ReconciliationRequired)
                        : Conflict());
            }

            if (_commands.TryGetValue(
                    request.ScopedCommandKey,
                    out var command))
            {
                return new ValueTask<WorldTransactionBeginResult>(
                    string.Equals(
                        command.OperationId,
                        request.OperationId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        command.RequestFingerprint,
                        request.RequestFingerprint,
                        StringComparison.Ordinal)
                        ? WorldTransactionBeginResult.NonTerminal(
                            WorldTransactionBeginStatus
                                .ReconciliationRequired,
                            WorldTransactionReasonCodes
                                .ReconciliationRequired)
                        : Conflict());
            }

            var address = request.ExpectedCoordinate.Address.StableKey;
            if (!_states.TryGetValue(address, out var cell))
            {
                return new ValueTask<WorldTransactionBeginResult>(
                    StoreAdmissionRejection(
                        request,
                        null,
                        null,
                        WorldTransactionReasonCodes.StateNotFound));
            }

            var mismatch = CoordinateMismatch(
                request.ExpectedCoordinate,
                cell.Snapshot.Coordinate);
            if (mismatch is not null)
            {
                return new ValueTask<WorldTransactionBeginResult>(
                    StoreAdmissionRejection(
                        request,
                        cell.Snapshot.Coordinate,
                        cell.Snapshot.StateDigest,
                        mismatch));
            }

            if (!IncarnationsMatch(
                    request.ExpectedIncarnations,
                    cell.Snapshot.EntityIncarnations))
            {
                return new ValueTask<WorldTransactionBeginResult>(
                    StoreAdmissionRejection(
                        request,
                        cell.Snapshot.Coordinate,
                        cell.Snapshot.StateDigest,
                        WorldTransactionReasonCodes.StaleIncarnation));
            }

            if (request.EventOccurrence is not null
                && _history.TryGetValue(
                    request.EventOccurrence.InstanceId,
                    out var recorded))
            {
                var outcome = recorded.IsEquivalentTo(
                    request.EventOccurrence)
                    ? WorldTransactionReasonCodes.EventAlreadyCommitted
                    : WorldTransactionReasonCodes.InvalidHistory;
                return new ValueTask<WorldTransactionBeginResult>(
                    StoreAdmissionRejection(
                        request,
                        cell.Snapshot.Coordinate,
                        cell.Snapshot.StateDigest,
                        outcome));
            }

            if (cell.Pending is not null)
            {
                return new ValueTask<WorldTransactionBeginResult>(
                    WorldTransactionBeginResult.NonTerminal(
                        WorldTransactionBeginStatus.Busy,
                        WorldTransactionReasonCodes.Busy));
            }

            var pending = new PendingOperation(
                request,
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                cell);
            cell.Pending = pending;
            _pendingByOperation.Add(request.ScopedOperationKey, pending);
            _commands.Add(
                request.ScopedCommandKey,
                new OperationIdentity(
                    request.OperationId,
                    request.RequestFingerprint));
            var transaction = new InMemoryTransaction(this, pending);
            return new ValueTask<WorldTransactionBeginResult>(
                WorldTransactionBeginResult.Acquired(transaction));
        }
    }

    public ValueTask<WorldTransactionInspectionResult> InspectAsync(
        WorldTransactionScope scope,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var normalizedOperation = WorldValidation.Required(
            operationId,
            nameof(operationId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var operationKey = WorldValidation.ComposeStableKey(
                scope.StableKey,
                normalizedOperation);
            if (_receiptsByOperation.TryGetValue(
                    operationKey,
                    out var receipt))
            {
                return new ValueTask<WorldTransactionInspectionResult>(
                    WorldTransactionInspectionResult.Terminal(receipt));
            }

            if (_pendingByOperation.TryGetValue(
                    operationKey,
                    out var pending))
            {
                return new ValueTask<WorldTransactionInspectionResult>(
                    WorldTransactionInspectionResult.Pending(
                        pending.Request));
            }

            return new ValueTask<WorldTransactionInspectionResult>(
                WorldTransactionInspectionResult.NotFound());
        }
    }

    public ValueTask<WorldTransactionReconciliationResult> ReconcileAsync(
        WorldTransactionScope scope,
        string operationId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var normalizedOperation = WorldValidation.Required(
            operationId,
            nameof(operationId));
        var normalizedFingerprint = WorldValidation.Required(
            requestFingerprint,
            nameof(requestFingerprint),
            128);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var operationKey = WorldValidation.ComposeStableKey(
                scope.StableKey,
                normalizedOperation);
            if (_receiptsByOperation.TryGetValue(
                    operationKey,
                    out var receipt))
            {
                return new ValueTask<
                    WorldTransactionReconciliationResult>(
                    string.Equals(
                        receipt.RequestFingerprint,
                        normalizedFingerprint,
                        StringComparison.Ordinal)
                        ? WorldTransactionReconciliationResult.Terminal(
                            receipt)
                        : ReconciliationConflict());
            }

            if (_pendingByOperation.TryGetValue(
                    operationKey,
                    out var pending))
            {
                return new ValueTask<
                    WorldTransactionReconciliationResult>(
                    string.Equals(
                        pending.Request.RequestFingerprint,
                        normalizedFingerprint,
                        StringComparison.Ordinal)
                        ? WorldTransactionReconciliationResult.NonTerminal(
                            WorldTransactionReconciliationStatus.Pending,
                            WorldTransactionReasonCodes
                                .ReconciliationRequired)
                        : ReconciliationConflict());
            }

            return new ValueTask<WorldTransactionReconciliationResult>(
                WorldTransactionReconciliationResult.NonTerminal(
                    WorldTransactionReconciliationStatus.NotFound,
                    WorldTransactionReasonCodes.StateNotFound));
        }
    }

    public ValueTask<WorldTransactionReconciliationResult>
        CancelPendingAsync(
            WorldTransactionScope scope,
            string operationId,
            string requestFingerprint,
            string outcomeCode,
            CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        var normalizedOperation = WorldValidation.Required(
            operationId,
            nameof(operationId));
        var normalizedFingerprint = WorldValidation.Required(
            requestFingerprint,
            nameof(requestFingerprint),
            128);
        var normalizedOutcome = WorldValidation.Required(
            outcomeCode,
            nameof(outcomeCode),
            96);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var operationKey = WorldValidation.ComposeStableKey(
                scope.StableKey,
                normalizedOperation);
            if (_receiptsByOperation.TryGetValue(
                    operationKey,
                    out var receipt))
            {
                return new ValueTask<
                    WorldTransactionReconciliationResult>(
                    string.Equals(
                        receipt.RequestFingerprint,
                        normalizedFingerprint,
                        StringComparison.Ordinal)
                        ? WorldTransactionReconciliationResult.Terminal(
                            receipt)
                        : ReconciliationConflict());
            }

            if (!_pendingByOperation.TryGetValue(
                    operationKey,
                    out var pending))
            {
                return new ValueTask<
                    WorldTransactionReconciliationResult>(
                    WorldTransactionReconciliationResult.NonTerminal(
                        WorldTransactionReconciliationStatus.NotFound,
                        WorldTransactionReasonCodes.StateNotFound));
            }

            if (!string.Equals(
                    pending.Request.RequestFingerprint,
                    normalizedFingerprint,
                    StringComparison.Ordinal))
            {
                return new ValueTask<
                    WorldTransactionReconciliationResult>(
                    ReconciliationConflict());
            }

            var cancelled = CreateReceipt(
                pending.Request,
                WorldCommandReceiptStatus.Cancelled,
                normalizedOutcome,
                pending.Cell.Snapshot.Coordinate,
                pending.Cell.Snapshot.StateDigest,
                null);
            StoreReceiptAndRelease(pending, cancelled);
            return new ValueTask<WorldTransactionReconciliationResult>(
                WorldTransactionReconciliationResult.Terminal(cancelled));
        }
    }

    public ValueTask<WorldEventDefinitionHistory> ReadDefinitionAsync(
        WorldEventDefinitionKey definition,
        CancellationToken cancellationToken)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_definitionHistory.TryGetValue(
                    definition.StableKey,
                    out var state))
            {
                return new ValueTask<WorldEventDefinitionHistory>(
                    WorldEventDefinitionHistory.Empty);
            }

            return new ValueTask<WorldEventDefinitionHistory>(
                new WorldEventDefinitionHistory(
                    state.OccurrenceCount,
                    state.LastOccurredAt));
        }
    }

    public ValueTask<WorldEventHistoryRecord?> FindInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var normalized = WorldValidation.Required(
            instanceId,
            nameof(instanceId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _history.TryGetValue(normalized, out var record);
            return new ValueTask<WorldEventHistoryRecord?>(record);
        }
    }

    public ValueTask<WorldEventHistoryAppendResult> TryAppendAsync(
        WorldEventHistoryRecord record,
        CancellationToken cancellationToken)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_history.TryGetValue(record.InstanceId, out var existing))
            {
                if (!existing.IsEquivalentTo(record))
                {
                    throw new WorldEventConfigurationException(
                        WorldEvolutionReasonCodes.InvalidHistory,
                        "An instance identifier maps to conflicting history.");
                }

                return new ValueTask<WorldEventHistoryAppendResult>(
                    WorldEventHistoryAppendResult.AlreadyExists);
            }

            var update = PrepareHistoryAppend(record);
            AppendHistory(record, update);
            return new ValueTask<WorldEventHistoryAppendResult>(
                WorldEventHistoryAppendResult.Appended);
        }
    }

    private WorldTransactionCommitResult CommitEvent(
        PendingOperation pending,
        string token,
        InMemoryStateDraft draft,
        WorldEffectReceipt effect)
    {
        lock (_sync)
        {
            if (!Owns(pending, token))
            {
                return WorldTransactionCommitResult.LeaseLost();
            }

            var occurrence = pending.Request.EventOccurrence
                             ?? throw new InvalidOperationException(
                                 "An event commit requires an occurrence.");
            if (_history.TryGetValue(
                    occurrence.InstanceId,
                    out var existing))
            {
                if (!existing.IsEquivalentTo(occurrence))
                {
                    throw new WorldEventConfigurationException(
                        WorldEvolutionReasonCodes.InvalidHistory,
                        "An instance identifier maps to conflicting history.");
                }

                return WorldTransactionCommitResult.LeaseLost();
            }

            var historyUpdate = PrepareHistoryAppend(occurrence);
            var stateChanged = draft.HasChanges;
            var nextCoordinate =
                pending.Cell.Snapshot.Coordinate.Advance(stateChanged);
            var nextState = draft.Build(nextCoordinate);
            var receipt = CreateReceipt(
                pending.Request,
                WorldCommandReceiptStatus.Applied,
                effect.OutcomeCode,
                nextCoordinate,
                nextState.StateDigest,
                effect);

            // These writes share one lock and become visible together.
            pending.Cell.Snapshot = nextState;
            AppendHistory(occurrence, historyUpdate);
            StoreReceiptAndRelease(pending, receipt);
            return WorldTransactionCommitResult.Committed(receipt);
        }
    }

    private WorldTransactionCommitResult CompleteWithoutMutation(
        PendingOperation pending,
        string token,
        WorldCommandReceiptStatus status,
        string outcomeCode,
        WorldEffectReceipt? effect)
    {
        if (status == WorldCommandReceiptStatus.Applied)
        {
            throw new ArgumentException(
                "A non-mutating completion cannot be applied.",
                nameof(status));
        }

        lock (_sync)
        {
            if (!Owns(pending, token))
            {
                return WorldTransactionCommitResult.LeaseLost();
            }

            var receipt = CreateReceipt(
                pending.Request,
                status,
                outcomeCode,
                pending.Cell.Snapshot.Coordinate,
                pending.Cell.Snapshot.StateDigest,
                effect);
            StoreReceiptAndRelease(pending, receipt);
            return WorldTransactionCommitResult.Committed(receipt);
        }
    }

    private bool Owns(PendingOperation pending, string token)
    {
        return ReferenceEquals(pending.Cell.Pending, pending)
               && string.Equals(
                   pending.Token,
                   token,
                   StringComparison.Ordinal)
               && pending.Cell.Snapshot.Coordinate.IsExactMatch(
                   pending.Request.ExpectedCoordinate);
    }

    private WorldTransactionBeginResult StoreAdmissionRejection(
        WorldTransactionRequest request,
        WorldAuthoritativeCoordinate? resultingCoordinate,
        string? stateDigest,
        string outcomeCode)
    {
        if (resultingCoordinate is not null
            && !request.ExpectedCoordinate.IsSameTimelineAs(
                resultingCoordinate))
        {
            resultingCoordinate = null;
            stateDigest = null;
        }

        var receipt = CreateReceipt(
            request,
            WorldCommandReceiptStatus.Rejected,
            outcomeCode,
            resultingCoordinate,
            stateDigest,
            null);
        _receiptsByOperation.Add(request.ScopedOperationKey, receipt);
        _commands.Add(
            request.ScopedCommandKey,
            new OperationIdentity(
                request.OperationId,
                request.RequestFingerprint));
        return WorldTransactionBeginResult.Terminal(receipt);
    }

    private static WorldCommandReceipt CreateReceipt(
        WorldTransactionRequest request,
        WorldCommandReceiptStatus status,
        string outcomeCode,
        WorldAuthoritativeCoordinate? coordinate,
        string? stateDigest,
        WorldEffectReceipt? effect)
    {
        return new WorldCommandReceipt(
            request,
            status,
            outcomeCode,
            coordinate,
            stateDigest,
            effect,
            status == WorldCommandReceiptStatus.Applied
                ? request.EventOccurrence?.InstanceId
                : null);
    }

    private void StoreReceiptAndRelease(
        PendingOperation pending,
        WorldCommandReceipt receipt)
    {
        _receiptsByOperation.Add(
            pending.Request.ScopedOperationKey,
            receipt);
        _pendingByOperation.Remove(pending.Request.ScopedOperationKey);
        if (ReferenceEquals(pending.Cell.Pending, pending))
        {
            pending.Cell.Pending = null;
        }
    }

    private DefinitionHistoryUpdate PrepareHistoryAppend(
        WorldEventHistoryRecord record)
    {
        var exists = _definitionHistory.TryGetValue(
                record.Definition.StableKey,
                out var definition);
        definition ??= new DefinitionState();
        var nextTime = definition.LastOccurredAt;
        if (record.OccurredAt is not null)
        {
            if (nextTime is null)
            {
                nextTime = record.OccurredAt;
            }
            else if (!nextTime.IsComparableTo(record.OccurredAt))
            {
                throw new WorldEventConfigurationException(
                    WorldEvolutionReasonCodes.InvalidHistory,
                    "Definition history mixes incompatible game clocks.");
            }
            else if (nextTime.CompareTo(record.OccurredAt) < 0)
            {
                nextTime = record.OccurredAt;
            }
        }

        long nextCount;
        checked
        {
            nextCount = definition.OccurrenceCount + 1;
        }

        return new DefinitionHistoryUpdate(
            definition,
            exists,
            nextCount,
            nextTime);
    }

    private void AppendHistory(
        WorldEventHistoryRecord record,
        DefinitionHistoryUpdate update)
    {
        _history.Add(record.InstanceId, record);
        if (!update.AlreadyExists)
        {
            _definitionHistory.Add(
                record.Definition.StableKey,
                update.State);
        }

        update.State.OccurrenceCount = update.NextOccurrenceCount;
        update.State.LastOccurredAt = update.NextLastOccurredAt;
    }

    private static bool IncarnationsMatch(
        IReadOnlyList<WorldEntityIncarnationExpectation> expected,
        IReadOnlyDictionary<string, long> actual)
    {
        foreach (var item in expected)
        {
            if (!actual.TryGetValue(item.EntityId, out var incarnation)
                || incarnation != item.Incarnation)
            {
                return false;
            }
        }

        return true;
    }

    private static string? CoordinateMismatch(
        WorldAuthoritativeCoordinate expected,
        WorldAuthoritativeCoordinate actual)
    {
        if (!expected.IsSameTimelineAs(actual))
        {
            return WorldTransactionReasonCodes.StaleCoordinate;
        }

        if (!string.Equals(
                expected.CatalogDigest,
                actual.CatalogDigest,
                StringComparison.Ordinal))
        {
            return WorldTransactionReasonCodes.StaleCatalog;
        }

        return expected.SaveRevision != actual.SaveRevision
               || expected.StateVersion != actual.StateVersion
            ? WorldTransactionReasonCodes.StaleVersion
            : null;
    }

    private static bool IsSameRequest(
        WorldTransactionRequest left,
        WorldTransactionRequest right)
    {
        return string.Equals(
            left.RequestFingerprint,
            right.RequestFingerprint,
            StringComparison.Ordinal);
    }

    private static bool SameScope(
        WorldAuthoritativeCoordinate left,
        WorldAuthoritativeCoordinate right)
    {
        return string.Equals(
                   left.WorldId,
                   right.WorldId,
                   StringComparison.Ordinal)
               && string.Equals(
                   left.TimelineId,
                   right.TimelineId,
                   StringComparison.Ordinal)
               && left.TimelineEpoch == right.TimelineEpoch;
    }

    private static WorldTransactionBeginResult Conflict()
    {
        return WorldTransactionBeginResult.NonTerminal(
            WorldTransactionBeginStatus.IdempotencyConflict,
            WorldTransactionReasonCodes.IdempotencyConflict);
    }

    private static WorldTransactionReconciliationResult
        ReconciliationConflict()
    {
        return WorldTransactionReconciliationResult.NonTerminal(
            WorldTransactionReconciliationStatus.IdempotencyConflict,
            WorldTransactionReasonCodes.IdempotencyConflict);
    }

    private sealed class StateCell
    {
        public StateCell(WorldAuthoritativeStateSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public WorldAuthoritativeStateSnapshot Snapshot { get; set; }

        public PendingOperation? Pending { get; set; }
    }

    private sealed class PendingOperation
    {
        public PendingOperation(
            WorldTransactionRequest request,
            string token,
            StateCell cell)
        {
            Request = request;
            Token = token;
            Cell = cell;
        }

        public WorldTransactionRequest Request { get; }

        public string Token { get; }

        public StateCell Cell { get; }
    }

    private sealed class OperationIdentity
    {
        public OperationIdentity(
            string operationId,
            string requestFingerprint)
        {
            OperationId = operationId;
            RequestFingerprint = requestFingerprint;
        }

        public string OperationId { get; }

        public string RequestFingerprint { get; }
    }

    private sealed class DefinitionState
    {
        public long OccurrenceCount { get; set; }

        public GameTimePoint? LastOccurredAt { get; set; }
    }

    private sealed class DefinitionHistoryUpdate
    {
        public DefinitionHistoryUpdate(
            DefinitionState state,
            bool alreadyExists,
            long nextOccurrenceCount,
            GameTimePoint? nextLastOccurredAt)
        {
            State = state;
            AlreadyExists = alreadyExists;
            NextOccurrenceCount = nextOccurrenceCount;
            NextLastOccurredAt = nextLastOccurredAt;
        }

        public DefinitionState State { get; }

        public bool AlreadyExists { get; }

        public long NextOccurrenceCount { get; }

        public GameTimePoint? NextLastOccurredAt { get; }
    }

    private sealed class InMemoryTransaction
        : IWorldAuthoritativeTransaction
    {
        private readonly InMemoryWorldAuthoritativeTransactionStore _owner;

        private readonly PendingOperation _pending;

        private readonly InMemoryStateDraft _draft;

        public InMemoryTransaction(
            InMemoryWorldAuthoritativeTransactionStore owner,
            PendingOperation pending)
        {
            _owner = owner;
            _pending = pending;
            _draft = new InMemoryStateDraft(pending.Cell.Snapshot);
        }

        public WorldTransactionRequest Request => _pending.Request;

        public WorldAuthoritativeStateSnapshot Source => _draft.Source;

        public IWorldStateDraft Draft => _draft;

        public ValueTask<WorldTransactionCommitResult> CommitEventAsync(
            WorldEffectReceipt effect,
            CancellationToken cancellationToken)
        {
            if (effect is null)
            {
                throw new ArgumentNullException(nameof(effect));
            }

            if (!effect.Applied)
            {
                throw new ArgumentException(
                    "An event commit requires an applied effect.",
                    nameof(effect));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldTransactionCommitResult>(
                _owner.CommitEvent(
                    _pending,
                    _pending.Token,
                    _draft,
                    effect));
        }

        public ValueTask<WorldTransactionCommitResult>
            CompleteWithoutMutationAsync(
                WorldCommandReceiptStatus status,
                string outcomeCode,
                WorldEffectReceipt? effect,
                CancellationToken cancellationToken)
        {
            var normalizedOutcome = WorldValidation.Required(
                outcomeCode,
                nameof(outcomeCode),
                96);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldTransactionCommitResult>(
                _owner.CompleteWithoutMutation(
                    _pending,
                    _pending.Token,
                    status,
                    normalizedOutcome,
                    effect));
        }

        public ValueTask DisposeAsync()
        {
            // Disposing does not silently release durable ownership. A crash
            // or abandoned lease remains pending until reconciliation.
            return default;
        }
    }

    private sealed class InMemoryStateDraft
        : IWorldStateDraft,
          IWorldIssuedIncarnationDraft
    {
        private JsonElement? _replacement;

        private Dictionary<string, long>? _incarnations;

        private Dictionary<string, HashSet<long>>? _issuedIncarnations;

        private int _issuedIncarnationCount;

        public InMemoryStateDraft(
            WorldAuthoritativeStateSnapshot source)
        {
            Source = source;
        }

        public WorldAuthoritativeStateSnapshot Source { get; }

        public JsonElement State => _replacement ?? Source.State;

        public string StateDigest =>
            _replacement.HasValue
                ? WorldStateJson.ComputeDigest(_replacement.Value)
                : Source.StateDigest;

        public IReadOnlyDictionary<string, long> EntityIncarnations =>
            _incarnations is null
                ? Source.EntityIncarnations
                : new ReadOnlyDictionary<string, long>(
                    new Dictionary<string, long>(
                        _incarnations,
                        StringComparer.Ordinal));

        public IReadOnlyList<WorldIssuedEntityIncarnation>
            IssuedEntityIncarnations =>
            new ReadOnlyCollection<WorldIssuedEntityIncarnation>(
                EnumerateIssuedIncarnations().ToArray());

        public bool HasChanges =>
            !string.Equals(
                StateDigest,
                Source.StateDigest,
                StringComparison.Ordinal)
            || !SameIncarnations(
                Source.EntityIncarnations,
                _incarnations)
            || _issuedIncarnations is not null;

        public void ReplaceState(JsonElement state)
        {
            WorldAuthoritativeStateSnapshot.ValidateState(
                state,
                nameof(state));
            _replacement = state.Clone();
        }

        public bool TryGetIncarnation(
            string entityId,
            out long incarnation)
        {
            var normalized = WorldValidation.Required(
                entityId,
                nameof(entityId));
            return (_incarnations ?? Source.EntityIncarnations)
                .TryGetValue(normalized, out incarnation);
        }

        public void SetIncarnation(string entityId, long incarnation)
        {
            var normalized = WorldValidation.Required(
                entityId,
                nameof(entityId));
            if (incarnation < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(incarnation));
            }

            var current = _incarnations ?? Source.EntityIncarnations;
            if (current.TryGetValue(normalized, out var existing)
                && existing == incarnation)
            {
                return;
            }

            if (TryGetLatestIssuedIncarnation(
                    normalized,
                    out var latest)
                && incarnation <= latest)
            {
                throw new InvalidOperationException(
                    "A new entity incarnation must be greater than every lifetime previously issued for that entity.");
            }

            if (!current.ContainsKey(normalized)
                && current.Count >= WorldValidation.MaximumParticipants)
            {
                throw new InvalidOperationException(
                    "The current entity-incarnation collection exceeds its item limit.");
            }

            AddIssuedIncarnation(normalized, incarnation);
            EnsureIncarnationCopy()[normalized] = incarnation;
        }

        public void RemoveIncarnation(string entityId)
        {
            var normalized = WorldValidation.Required(
                entityId,
                nameof(entityId));
            EnsureIncarnationCopy().Remove(normalized);
        }

        public WorldAuthoritativeStateSnapshot Build(
            WorldAuthoritativeCoordinate coordinate)
        {
            return new WorldAuthoritativeStateSnapshot(
                coordinate,
                State,
                _incarnations ?? Source.EntityIncarnations,
                EnumerateIssuedIncarnations());
        }

        private Dictionary<string, long> EnsureIncarnationCopy()
        {
            return _incarnations ??= new Dictionary<string, long>(
                Source.EntityIncarnations,
                StringComparer.Ordinal);
        }

        private bool TryGetLatestIssuedIncarnation(
            string entityId,
            out long incarnation)
        {
            if (_issuedIncarnations is not null)
            {
                if (_issuedIncarnations.TryGetValue(
                        entityId,
                        out var incarnations)
                    && incarnations.Count != 0)
                {
                    incarnation = incarnations.Max();
                    return true;
                }

                incarnation = default;
                return false;
            }

            return Source.TryGetLatestIssuedIncarnation(
                entityId,
                out incarnation);
        }

        private void AddIssuedIncarnation(
            string entityId,
            long incarnation)
        {
            var issued = EnsureIssuedIncarnationCopy();
            if (_issuedIncarnationCount
                >= WorldAuthoritativeStateSnapshot
                    .MaximumIssuedIncarnationCount)
            {
                throw new InvalidOperationException(
                    "The issued-incarnation ledger exceeds its item limit.");
            }

            if (!issued.TryGetValue(entityId, out var incarnations))
            {
                if (issued.Count
                    >= WorldAuthoritativeStateSnapshot
                        .MaximumIssuedEntityCount)
                {
                    throw new InvalidOperationException(
                        "The issued-incarnation ledger exceeds its entity limit.");
                }

                incarnations = new HashSet<long>();
                issued.Add(entityId, incarnations);
            }

            if (!incarnations.Add(incarnation))
            {
                throw new InvalidOperationException(
                    "An issued entity incarnation cannot be reused.");
            }

            _issuedIncarnationCount++;
        }

        private Dictionary<string, HashSet<long>>
            EnsureIssuedIncarnationCopy()
        {
            if (_issuedIncarnations is not null)
            {
                return _issuedIncarnations;
            }

            _issuedIncarnations =
                new Dictionary<string, HashSet<long>>(
                    StringComparer.Ordinal);
            foreach (var item in Source.IssuedEntityIncarnations)
            {
                if (!_issuedIncarnations.TryGetValue(
                        item.EntityId,
                        out var incarnations))
                {
                    incarnations = new HashSet<long>();
                    _issuedIncarnations.Add(
                        item.EntityId,
                        incarnations);
                }

                incarnations.Add(item.Incarnation);
            }

            _issuedIncarnationCount =
                Source.IssuedEntityIncarnations.Count;
            return _issuedIncarnations;
        }

        private IEnumerable<WorldIssuedEntityIncarnation>
            EnumerateIssuedIncarnations()
        {
            if (_issuedIncarnations is null)
            {
                return Source.IssuedEntityIncarnations;
            }

            return _issuedIncarnations
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(
                    pair => pair.Value
                        .OrderBy(value => value)
                        .Select(
                            value =>
                                new WorldIssuedEntityIncarnation(
                                    pair.Key,
                                    value)));
        }

        private static bool SameIncarnations(
            IReadOnlyDictionary<string, long> source,
            IReadOnlyDictionary<string, long>? changed)
        {
            if (changed is null)
            {
                return true;
            }

            if (source.Count != changed.Count)
            {
                return false;
            }

            foreach (var pair in source)
            {
                if (!changed.TryGetValue(pair.Key, out var value)
                    || value != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

internal static class WorldTransactionIdentity
{
    public static string ComputeRequestFingerprint(
        WorldTransactionRequest request)
    {
        return Hash(
            writer =>
            {
                Write(writer, "world-transaction-request-v1");
                Write(writer, request.OperationId);
                Write(writer, request.CommandId);
                Write(writer, request.CommandPayloadDigest);
                WriteCoordinate(writer, request.ExpectedCoordinate);
                writer.Write(request.ExpectedIncarnations.Count);
                foreach (var item in request.ExpectedIncarnations)
                {
                    Write(writer, item.EntityId);
                    writer.Write(item.Incarnation);
                }

                var occurrence = request.EventOccurrence;
                writer.Write(occurrence is not null);
                if (occurrence is not null)
                {
                    Write(writer, occurrence.InstanceId);
                    Write(writer, occurrence.PlanFingerprint);
                    Write(writer, occurrence.Definition.StableKey);
                }
            });
    }

    public static string ComputeReceiptId(WorldCommandReceipt receipt)
    {
        return "wrcpt_" + Hash(
            writer =>
            {
                Write(writer, "world-command-receipt-v1");
                Write(writer, receipt.RequestFingerprint);
                writer.Write((int)receipt.Status);
                Write(writer, receipt.OutcomeCode);
                WriteOptionalCoordinate(
                    writer,
                    receipt.ResultingCoordinate);
                Write(writer, receipt.ResultingStateDigest);
                Write(writer, receipt.EventInstanceId);
                writer.Write(receipt.Effect is not null);
                if (receipt.Effect is not null)
                {
                    writer.Write(receipt.Effect.Applied);
                    Write(writer, receipt.Effect.OutcomeCode);
                    Write(
                        writer,
                        receipt.Effect.TypedResult.HasValue
                            ? CanonicalJsonDigest.ComputeSha256(
                                receipt.Effect.TypedResult.Value)
                            : null);
                }
            });
    }

    private static string Hash(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(
                   buffer,
                   new UTF8Encoding(
                       encoderShouldEmitUTF8Identifier: false,
                       throwOnInvalidBytes: true),
                   leaveOpen: true))
        {
            write(writer);
        }

        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(buffer.ToArray());
        var text = new StringBuilder(digest.Length * 2);
        foreach (var value in digest)
        {
            _ = text.Append(
                value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static void WriteCoordinate(
        BinaryWriter writer,
        WorldAuthoritativeCoordinate coordinate)
    {
        Write(writer, coordinate.WorldId);
        Write(writer, coordinate.TimelineId);
        writer.Write(coordinate.TimelineEpoch);
        writer.Write(coordinate.SaveRevision);
        writer.Write(coordinate.StateVersion);
        Write(writer, coordinate.CatalogDigest);
    }

    private static void WriteOptionalCoordinate(
        BinaryWriter writer,
        WorldAuthoritativeCoordinate? coordinate)
    {
        writer.Write(coordinate is not null);
        if (coordinate is not null)
        {
            WriteCoordinate(writer, coordinate);
        }
    }

    private static void Write(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }
}

internal static class WorldStateJson
{
    private static readonly byte[] DigestDomain =
        Encoding.UTF8.GetBytes("world-authoritative-state-v1\0");

    public static void RejectNumbers(
        JsonElement value,
        string parameterName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                throw new ArgumentException(
                    "Authoritative numeric values require canonical string "
                    + "encoding and a portable schema.",
                    parameterName);
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new ArgumentException(
                            "Authoritative state cannot contain duplicate "
                            + "object properties.",
                            parameterName);
                    }

                    RejectNumbers(property.Value, parameterName);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    RejectNumbers(item, parameterName);
                }

                break;
            case JsonValueKind.Undefined:
                throw new ArgumentException(
                    "Undefined JSON is not authoritative state.",
                    parameterName);
        }
    }

    public static string ComputeDigest(JsonElement value)
    {
        using var stream = new HashingWriteStream(DigestDomain);
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            WriteCanonical(writer, value);
            writer.Flush();
        }

        return stream.GetDigest();
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException(
                    "The value cannot be canonicalized as world state.",
                    nameof(value));
        }
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private bool _completed;

        public HashingWriteStream(byte[] domain)
        {
            _hash.AppendData(domain);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_completed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            if (_completed)
            {
                throw new ObjectDisposedException(nameof(HashingWriteStream));
            }

            _hash.AppendData(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_completed)
            {
                throw new ObjectDisposedException(nameof(HashingWriteStream));
            }

            _hash.AppendData(buffer);
        }

        public string GetDigest()
        {
            if (_completed)
            {
                throw new InvalidOperationException(
                    "The digest has already been completed.");
            }

            _completed = true;
            var digest = _hash.GetHashAndReset();
            var text = new StringBuilder(digest.Length * 2);
            foreach (var item in digest)
            {
                text.Append(
                    item.ToString(
                        "x2",
                        CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
