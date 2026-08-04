using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Adds an authoritative resulting game coordinate to a terminal host receipt.
/// The optional previous coordinate is an explicit baseline assertion. Every
/// coordinate-bearing receipt in one decision window must report the same
/// final coordinate.
/// </summary>
public static class GameContextReceiptEnvelope
{
    public const string ResultingExtensionName = "resultingGameContext";

    public const string PreviousExtensionName = "previousGameContext";

    internal const int MaxCoordinateUtf8Bytes = 16_384;

    private static readonly JsonValueLimits CoordinateLimits = new(
        maxUtf8Bytes: MaxCoordinateUtf8Bytes,
        maxDepth: 8,
        maxNodes: 256,
        maxStringUtf8Bytes: 512,
        maxContainerItems: 64);

    public static void AttachResulting(
        ActionReceipt receipt,
        GameContextCoordinate resulting,
        GameContextCoordinate? previous = null)
    {
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        if (resulting is null)
        {
            throw new ArgumentNullException(nameof(resulting));
        }

        if (string.Equals(
                receipt.Status,
                ReceiptStatuses.Unknown,
                StringComparison.Ordinal))
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes
                    .NonterminalReceiptCannotAdvance,
                "An unknown receipt cannot carry a resulting game context.");
        }

        receipt.Extensions ??= new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        var additions = receipt.Extensions.ContainsKey(
            ResultingExtensionName)
            ? 0
            : 1;
        if (previous is not null
            && !receipt.Extensions.ContainsKey(PreviousExtensionName))
        {
            additions++;
        }

        if (receipt.Extensions.Count
            > ProtocolLimits.MaxProtocolExtensions - additions)
        {
            throw new RuntimeContentLimitException(
                nameof(receipt),
                "receipt_extensions_exceeded",
                "The receipt has no capacity for game-context transition "
                + "metadata.");
        }

        var resultingJson = GameContextEnvelope.ToJson(resulting);
        _ = ReadCoordinate(
            resultingJson,
            nameof(resulting));
        JsonElement? previousJson = null;
        if (previous is not null)
        {
            previousJson = GameContextEnvelope.ToJson(previous);
            _ = ReadCoordinate(
                previousJson.Value,
                nameof(previous));
            GameContextAdvancementPlanner.ValidateForward(
                previous,
                resulting,
                run: null);
        }

        receipt.Extensions[ResultingExtensionName] =
            resultingJson.Clone();
        if (previousJson.HasValue)
        {
            receipt.Extensions[PreviousExtensionName] =
                previousJson.Value.Clone();
        }
        else
        {
            receipt.Extensions.Remove(PreviousExtensionName);
        }
    }

    public static bool TryReadResulting(
        ActionReceipt receipt,
        out GameContextCoordinate? resulting)
    {
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        resulting = null;
        if (receipt.Extensions is null
            || !receipt.Extensions.TryGetValue(
                ResultingExtensionName,
                out var value))
        {
            return false;
        }

        try
        {
            var transition = ValidateAndRead(receipt);
            resulting = transition?.Resulting;
            return transition is not null;
        }
        catch (GameContextAdvancementException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static GameContextReceiptTransition? ValidateAndRead(
        ActionReceipt receipt)
    {
        if (receipt.Extensions is null)
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.InvalidReceiptEnvelope,
                "A receipt extension bag is required.");
        }

        var hasResult = receipt.Extensions.TryGetValue(
            ResultingExtensionName,
            out var resultingJson);
        var hasPrevious = receipt.Extensions.TryGetValue(
            PreviousExtensionName,
            out var previousJson);
        if (!hasResult)
        {
            if (hasPrevious)
            {
                throw new GameContextAdvancementException(
                    GameContextAdvancementReasonCodes.InvalidReceiptEnvelope,
                    "A previous game context requires a resulting game "
                    + "context.");
            }

            return null;
        }

        if (string.Equals(
                receipt.Status,
                ReceiptStatuses.Unknown,
                StringComparison.Ordinal))
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes
                    .NonterminalReceiptCannotAdvance,
                "An unknown receipt cannot advance game context.");
        }

        var resulting = ReadCoordinate(
            resultingJson,
            ResultingExtensionName);
        var previous = hasPrevious
            ? ReadCoordinate(previousJson, PreviousExtensionName)
            : null;
        return new GameContextReceiptTransition(
            RuntimeGuard.RequiredId(
                receipt.OperationId,
                nameof(receipt.OperationId)),
            previous,
            resulting);
    }

    internal static bool NormalizeForRun(
        ActionReceipt receipt,
        AgentRun run)
    {
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        var transition = ValidateAndRead(receipt);
        if (transition is null)
        {
            return false;
        }

        var bound = BindToRun(transition, run);
        var changed =
            !ReferenceEquals(bound.Resulting, transition.Resulting)
            || !ReferenceEquals(bound.Previous, transition.Previous);
        if (!changed)
        {
            return false;
        }

        receipt.Extensions[ResultingExtensionName] =
            GameContextEnvelope.ToJson(bound.Resulting);
        if (bound.Previous is not null)
        {
            receipt.Extensions[PreviousExtensionName] =
                GameContextEnvelope.ToJson(bound.Previous);
        }

        return true;
    }

    internal static GameContextReceiptTransition BindToRun(
        GameContextReceiptTransition transition,
        AgentRun run)
    {
        if (transition is null)
        {
            throw new ArgumentNullException(nameof(transition));
        }

        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        var previous = transition.Previous is null
            ? null
            : BindSession(transition.Previous, run);
        var resulting = BindSession(transition.Resulting, run);
        if (previous is not null)
        {
            GameContextAdvancementPlanner.ValidateForward(
                previous,
                resulting,
                run);
        }

        return new GameContextReceiptTransition(
            transition.OperationId,
            previous,
            resulting);
    }

    private static GameContextCoordinate BindSession(
        GameContextCoordinate coordinate,
        AgentRun run)
    {
        if (coordinate.SessionId is not null
            && !string.Equals(
                coordinate.SessionId,
                run.SessionId,
                StringComparison.Ordinal))
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.IdentityMismatch,
                "A receipt coordinate escapes the immutable run session.");
        }

        if (string.Equals(
                coordinate.SessionId,
                run.SessionId,
                StringComparison.Ordinal))
        {
            return coordinate;
        }

        return new GameContextCoordinate(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.SaveRevision,
            coordinate.Observer,
            coordinate.SceneId,
            coordinate.RegionId,
            coordinate.StateVersion,
            coordinate.GameTime,
            coordinate.Causality,
            run.SessionId);
    }

    internal static GameContextCoordinate ReadCoordinate(
        JsonElement value,
        string parameterName)
    {
        try
        {
            JsonValueInspector.ValidateAndMeasure(
                value,
                CoordinateLimits,
                parameterName);
        }
        catch (RuntimeContentLimitException exception)
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.CoordinateLimitExceeded,
                "A game-context coordinate exceeds its bounded JSON limits.",
                exception);
        }

        if (!HasStrictCoordinateShape(value)
            || !GameContextEnvelope.TryRead(value, out var coordinate)
            || coordinate is null)
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.InvalidReceiptEnvelope,
                "A game-context coordinate is malformed or contains "
                + "unsupported fields.");
        }

        return coordinate;
    }

    private static bool HasStrictCoordinateShape(JsonElement value)
    {
        return HasProperties(
                   value,
                   required: new[]
                   {
                       "worldId",
                       "timelineId",
                       "saveRevision"
                   },
                   optional: new[]
                   {
                       "observer",
                       "sceneId",
                       "regionId",
                       "stateVersion",
                       "sessionId",
                       "gameTime",
                       "causality"
                   })
               && (!value.TryGetProperty("observer", out var observer)
                   || HasProperties(
                       observer,
                       required: new[] { "entityId", "incarnation" },
                       optional: Array.Empty<string>()))
               && (!value.TryGetProperty("gameTime", out var gameTime)
                   || HasProperties(
                       gameTime,
                       required: new[]
                       {
                           "clockId",
                           "timelineId",
                           "epoch",
                           "tick"
                       },
                       optional: Array.Empty<string>()))
               && (!value.TryGetProperty("causality", out var causality)
                   || HasProperties(
                       causality,
                       required: new[]
                       {
                           "eventId",
                           "basedOnStateVersion",
                           "parentEventIds"
                       },
                       optional: Array.Empty<string>()));
    }

    private static bool HasProperties(
        JsonElement value,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var allowed = required
            .Concat(optional)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return required.All(seen.Contains);
    }
}

public static class GameContextAdvancementReasonCodes
{
    public const string InvalidReceiptEnvelope =
        "game_context_receipt_invalid";

    public const string CoordinateLimitExceeded =
        "game_context_coordinate_limit_exceeded";

    public const string NonterminalReceiptCannotAdvance =
        "game_context_receipt_nonterminal";

    public const string IdentityMismatch =
        "game_context_identity_mismatch";

    public const string CoordinateRegression =
        "game_context_coordinate_regression";

    public const string TransitionConflict =
        "game_context_transition_conflict";

    public const string RecoveryEvidenceInvalid =
        "game_context_recovery_evidence_invalid";
}

public sealed class GameContextAdvancementException : InvalidOperationException
{
    internal GameContextAdvancementException(
        string reasonCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = RuntimeGuard.RequiredUtf8(
            reasonCode,
            96,
            nameof(reasonCode));
    }

    public string ReasonCode { get; }
}

internal sealed class GameContextReceiptTransition
{
    public GameContextReceiptTransition(
        string operationId,
        GameContextCoordinate? previous,
        GameContextCoordinate resulting)
    {
        OperationId = operationId;
        Previous = previous;
        Resulting = resulting;
    }

    public string OperationId { get; }

    public GameContextCoordinate? Previous { get; }

    public GameContextCoordinate Resulting { get; }
}

internal sealed class GameContextAdvancementPlan
{
    public GameContextAdvancementPlan(
        GameContextCoordinate previous,
        GameContextCoordinate resulting,
        IReadOnlyList<string> operationIds)
    {
        Previous = previous;
        Resulting = resulting;
        OperationIds = new ReadOnlyCollection<string>(
            operationIds.ToArray());
        EvidenceDigest =
            GameContextAdvancementJournalCodec.ComputeEvidenceDigest(
                Previous,
                Resulting,
                OperationIds);
    }

    public GameContextCoordinate Previous { get; }

    public GameContextCoordinate Resulting { get; }

    public IReadOnlyList<string> OperationIds { get; }

    public string EvidenceDigest { get; }
}

internal static class GameContextAdvancementPlanner
{
    private const int MaximumReceiptsPerTurn = 4_096;

    public static GameContextAdvancementPlan? Plan(
        AgentRun run,
        IReadOnlyList<ActionRequest> requests,
        IReadOnlyList<ActionReceipt> receipts)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (receipts is null)
        {
            throw new ArgumentNullException(nameof(receipts));
        }

        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        if (receipts.Count > MaximumReceiptsPerTurn
            || requests.Count > MaximumReceiptsPerTurn)
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.CoordinateLimitExceeded,
                "A turn contains too many receipts for game-context "
                + "advancement.");
        }

        var requestsByOperation = new Dictionary<string, ActionRequest>(
            requests.Count,
            StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (request is null
                || !requestsByOperation.TryAdd(
                    RuntimeGuard.RequiredId(
                        request.OperationId,
                        nameof(requests)),
                    request))
            {
                throw Conflict(
                    "An action request collection contains null or "
                    + "duplicate operations.");
            }
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var transitions = new List<GameContextReceiptTransition>();
        foreach (var receipt in receipts)
        {
            if (receipt is null)
            {
                throw new GameContextAdvancementException(
                    GameContextAdvancementReasonCodes.InvalidReceiptEnvelope,
                    "A terminal receipt collection contains a null item.");
            }

            if (string.Equals(
                    receipt.Status,
                    ReceiptStatuses.Unknown,
                    StringComparison.Ordinal))
            {
                throw new GameContextAdvancementException(
                    GameContextAdvancementReasonCodes
                        .NonterminalReceiptCannotAdvance,
                    "Game context cannot advance while a receipt is "
                    + "unknown.");
            }

            var operationId = RuntimeGuard.RequiredId(
                receipt.OperationId,
                nameof(receipts));
            if (!operationIds.Add(operationId))
            {
                throw Conflict(
                    "A terminal receipt collection contains duplicate "
                    + "operations.");
            }

            if (!requestsByOperation.ContainsKey(operationId))
            {
                throw Conflict(
                    "A terminal receipt has no matching action request.");
            }

            var transition =
                GameContextReceiptEnvelope.ValidateAndRead(receipt);
            if (transition is not null)
            {
                transitions.Add(
                    GameContextReceiptEnvelope.BindToRun(
                        transition,
                        run));
            }
        }

        if (transitions.Count == 0)
        {
            return null;
        }

        if (requestsByOperation.Count != operationIds.Count
            || requestsByOperation.Keys.Any(
                operationId => !operationIds.Contains(operationId)))
        {
            throw Conflict(
                "Game context cannot advance until every action request "
                + "has a terminal receipt.");
        }

        var current = GameContextEnvelope.ValidateForRun(
            run,
            nameof(run))
            ?? throw Conflict(
                "A resulting coordinate requires an established run "
                + "coordinate.");
        var sortedOperationIds = operationIds
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var distinctResults = transitions
            .GroupBy(
                item => CoordinateKey(item.Resulting),
                StringComparer.Ordinal)
            .ToArray();
        if (distinctResults.Length != 1)
        {
            throw Conflict(
                "All coordinate-bearing receipts in one decision window "
                + "must report the same resulting coordinate.");
        }

        var resulting = distinctResults[0].First().Resulting;
        foreach (var operationId in sortedOperationIds)
        {
            ValidateRequestBinding(
                run,
                requestsByOperation[operationId],
                current);
        }

        foreach (var transition in transitions)
        {
            if (transition.Previous is not null
                && !Equivalent(transition.Previous, current))
            {
                throw Conflict(
                    "A receipt predecessor does not match the decision "
                    + "window's initial coordinate.");
            }
        }
        ValidateForward(current, resulting, run);

        if (Equivalent(current, resulting))
        {
            return null;
        }

        return new GameContextAdvancementPlan(
            current,
            resulting,
            sortedOperationIds);
    }

    internal static void ValidateForward(
        GameContextCoordinate previous,
        GameContextCoordinate resulting,
        AgentRun? run)
    {
        if (previous is null)
        {
            throw new ArgumentNullException(nameof(previous));
        }

        if (resulting is null)
        {
            throw new ArgumentNullException(nameof(resulting));
        }

        if (!string.Equals(
                previous.WorldId,
                resulting.WorldId,
                StringComparison.Ordinal)
            || run is not null
            && (!string.Equals(
                    run.WorldId,
                    previous.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    run.WorldId,
                    resulting.WorldId,
                    StringComparison.Ordinal)))
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.IdentityMismatch,
                "A resulting coordinate changes the run world.");
        }

        if (!string.Equals(
                previous.TimelineId,
                resulting.TimelineId,
                StringComparison.Ordinal))
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.IdentityMismatch,
                "A resulting coordinate changes the active timeline.");
        }

        var sessionMismatch = run is null
            ? previous.SessionId is not null
              && resulting.SessionId is not null
              && !string.Equals(
                  previous.SessionId,
                  resulting.SessionId,
                  StringComparison.Ordinal)
            : (previous.SessionId is not null
               && !string.Equals(
                   previous.SessionId,
                   run.SessionId,
                   StringComparison.Ordinal)
               || resulting.SessionId is not null
               && !string.Equals(
                   resulting.SessionId,
                   run.SessionId,
                   StringComparison.Ordinal));
        if (sessionMismatch)
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.IdentityMismatch,
                "A resulting coordinate escapes the immutable run "
                + "session.");
        }

        if (!SameObserver(previous.Observer, resulting.Observer))
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.IdentityMismatch,
                "A resulting coordinate changes the observer entity "
                + "incarnation.");
        }

        if (resulting.SaveRevision < previous.SaveRevision)
        {
            throw Regression(
                "A resulting coordinate decreases the save revision.");
        }

        if (previous.GameTime is not null)
        {
            if (resulting.GameTime is null
                || !string.Equals(
                    previous.GameTime.ClockId,
                    resulting.GameTime.ClockId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    previous.GameTime.TimelineId,
                    resulting.GameTime.TimelineId,
                    StringComparison.Ordinal)
                || resulting.GameTime.Epoch < previous.GameTime.Epoch
                || resulting.GameTime.Epoch == previous.GameTime.Epoch
                && resulting.GameTime.Tick < previous.GameTime.Tick)
            {
                throw Regression(
                    "A resulting game-time point is absent, incomparable, "
                    + "or earlier.");
            }
        }

        if (previous.StateVersion is not null
            && resulting.StateVersion is null)
        {
            throw Regression(
                "A resulting coordinate removes its state-version fence.");
        }

        if (previous.Causality is not null)
        {
            if (resulting.Causality is null)
            {
                throw Regression(
                    "A resulting coordinate removes its causal stamp.");
            }

            if (string.Equals(
                    previous.Causality.EventId,
                    resulting.Causality.EventId,
                    StringComparison.Ordinal))
            {
                if (!string.Equals(
                        previous.Causality.BasedOnStateVersion,
                        resulting.Causality.BasedOnStateVersion,
                        StringComparison.Ordinal)
                    || !previous.Causality.ParentEventIds.SequenceEqual(
                        resulting.Causality.ParentEventIds,
                        StringComparer.Ordinal))
                {
                    throw Regression(
                        "A resulting coordinate rewrites an existing causal "
                        + "event.");
                }
            }
            else if (!resulting.Causality.ParentEventIds.Contains(
                         previous.Causality.EventId,
                         StringComparer.Ordinal))
            {
                throw Regression(
                    "A resulting causal event does not name the previous "
                    + "event as a parent.");
            }
        }
    }

    internal static bool Equivalent(
        GameContextCoordinate left,
        GameContextCoordinate right)
    {
        return string.Equals(
            CoordinateKey(left),
            CoordinateKey(right),
            StringComparison.Ordinal);
    }

    internal static string CoordinateKey(GameContextCoordinate coordinate)
    {
        return CanonicalJsonDigest.ComputeSha256(
            GameContextEnvelope.ToJson(coordinate));
    }

    private static bool SameObserver(
        GameEntityIdentity? left,
        GameEntityIdentity? right)
    {
        return left is null
            ? right is null
            : left.IsSameIncarnation(right);
    }

    private static GameContextAdvancementException Conflict(string message)
    {
        return new GameContextAdvancementException(
            GameContextAdvancementReasonCodes.TransitionConflict,
            message);
    }

    private static GameContextAdvancementException Regression(string message)
    {
        return new GameContextAdvancementException(
            GameContextAdvancementReasonCodes.CoordinateRegression,
            message);
    }

    private static void ValidateRequestBinding(
        AgentRun run,
        ActionRequest request,
        GameContextCoordinate source)
    {
        if (!string.Equals(
                request.RunId,
                run.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                request.AgentId,
                run.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                request.WorldId,
                run.WorldId,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(run.CurrentTurnId)
            || !string.Equals(
                request.TurnId,
                run.CurrentTurnId,
                StringComparison.Ordinal)
            || !request.Extensions.TryGetValue(
                GameContextEnvelope.ExtensionName,
                out var requestCoordinateJson))
        {
            throw Conflict(
                "A resulting coordinate is not bound to an action request "
                + "from the active run coordinate.");
        }

        GameContextCoordinate requestCoordinate;
        try
        {
            requestCoordinate = GameContextReceiptEnvelope.ReadCoordinate(
                requestCoordinateJson,
                GameContextEnvelope.ExtensionName);
        }
        catch (GameContextAdvancementException exception)
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.TransitionConflict,
                "An action request contains an invalid source coordinate.",
                exception);
        }

        ValidateForward(requestCoordinate, requestCoordinate, run);
        var basedOnStateVersion = source.StateVersion
                                  ?? source.Causality
                                      ?.BasedOnStateVersion;
        if (!Equivalent(requestCoordinate, source)
            || !string.Equals(
                request.BasedOnStateVersion,
                basedOnStateVersion,
                StringComparison.Ordinal))
        {
            throw Conflict(
                "A receipt transition predecessor does not match its "
                + "action request's exact based-on coordinate.");
        }
    }
}

internal static class GameContextAdvancementJournalCodec
{
    public const string PreviousExtensionName =
        "gameContextAdvancePrevious";

    public const string ResultingExtensionName =
        "gameContextAdvanceResulting";

    public const string SourceOperationsExtensionName =
        "gameContextAdvanceOperations";

    public static IReadOnlyDictionary<string, JsonElement> EncodeExtensions(
        GameContextAdvancementPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        return new ReadOnlyDictionary<string, JsonElement>(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [PreviousExtensionName] =
                    GameContextEnvelope.ToJson(plan.Previous),
                [ResultingExtensionName] =
                    GameContextEnvelope.ToJson(plan.Resulting),
                [SourceOperationsExtensionName] =
                    JsonArrayBuilder.Array(
                        plan.OperationIds.Select(JsonArrayBuilder.String))
            });
    }

    public static string EventIdSuffix(
        string turnId,
        GameContextAdvancementPlan plan)
    {
        return "game-context-advanced:"
               + RuntimeGuard.RequiredId(turnId, nameof(turnId))
               + ":"
               + plan.EvidenceDigest;
    }

    public static string ComputeEvidenceDigest(
        GameContextCoordinate previous,
        GameContextCoordinate resulting,
        IReadOnlyList<string> operationIds)
    {
        return CanonicalJsonDigest.ComputeSha256(
            JsonArrayBuilder.Object(
                ("previous", GameContextEnvelope.ToJson(previous)),
                ("resulting", GameContextEnvelope.ToJson(resulting)),
                ("operationIds", JsonArrayBuilder.Array(
                    operationIds.Select(JsonArrayBuilder.String)))));
    }

    public static void ValidateCheckpoint(
        RuntimeEvent runtimeEvent,
        AgentRun previousRun,
        AgentRun candidateRun)
    {
        if (runtimeEvent.Extensions.Count != 3
            || !runtimeEvent.Extensions.TryGetValue(
                PreviousExtensionName,
                out var previousJson)
            || !runtimeEvent.Extensions.TryGetValue(
                ResultingExtensionName,
                out var resultingJson)
            || !runtimeEvent.Extensions.TryGetValue(
                SourceOperationsExtensionName,
                out var operationsJson))
        {
            throw RecoveryInvalid(
                "A game-context checkpoint has incomplete evidence.");
        }

        var previous = ReadRecoveryCoordinate(
            previousJson,
            PreviousExtensionName);
        var resulting = ReadRecoveryCoordinate(
            resultingJson,
            ResultingExtensionName);
        var operationIds = ReadOperationIds(operationsJson);
        if (string.IsNullOrWhiteSpace(runtimeEvent.TurnId)
            || string.IsNullOrWhiteSpace(previousRun.CurrentTurnId)
            || !string.Equals(
                runtimeEvent.TurnId,
                previousRun.CurrentTurnId,
                StringComparison.Ordinal)
            || !string.Equals(
                candidateRun.CurrentTurnId,
                previousRun.CurrentTurnId,
                StringComparison.Ordinal)
            || previousRun.State is not RunStates.WaitingForAction
                and not RunStates.Reconciling
            || !string.Equals(
                candidateRun.State,
                previousRun.State,
                StringComparison.Ordinal))
        {
            throw RecoveryInvalid(
                "A game-context checkpoint is outside its active action "
                + "turn.");
        }

        var previousCoordinate = GameContextEnvelope.ValidateForRun(
            previousRun,
            nameof(previousRun))
            ?? throw RecoveryInvalid(
                "A game-context checkpoint has no previous coordinate.");
        var resultingCoordinate = GameContextEnvelope.ValidateForRun(
            candidateRun,
            nameof(candidateRun))
            ?? throw RecoveryInvalid(
                "A game-context checkpoint has no resulting coordinate.");
        if (!GameContextAdvancementPlanner.Equivalent(
                previous,
                previousCoordinate)
            || !GameContextAdvancementPlanner.Equivalent(
                resulting,
                resultingCoordinate))
        {
            throw RecoveryInvalid(
                "A game-context checkpoint evidence does not match its run "
                + "snapshots.");
        }

        GameContextAdvancementPlanner.ValidateForward(
            previous,
            resulting,
            previousRun);
        var digest = ComputeEvidenceDigest(
            previous,
            resulting,
            operationIds);
        var expectedEventId = RuntimeEventIdDerivation.Derive(
            runtimeEvent.RunId,
            "game-context-advanced:"
            + runtimeEvent.TurnId
            + ":"
            + digest);
        if (!string.Equals(
                runtimeEvent.EventId,
                expectedEventId,
                StringComparison.Ordinal))
        {
            throw RecoveryInvalid(
                "A game-context checkpoint has an inconsistent event "
                + "identity.");
        }
    }

    public static void ValidateReceiptEvidence(
        RuntimeEvent runtimeEvent,
        AgentRun previousRun,
        AgentRun candidateRun,
        IReadOnlyList<ActionRequest> requests,
        IReadOnlyList<ActionReceipt> receipts)
    {
        ValidateCheckpoint(runtimeEvent, previousRun, candidateRun);
        var plan = GameContextAdvancementPlanner.Plan(
            previousRun,
            requests,
            receipts)
            ?? throw RecoveryInvalid(
                "A game-context checkpoint has no advancing terminal "
                + "receipt.");
        var evidenceOperationIds = ReadOperationIds(
            runtimeEvent.Extensions[SourceOperationsExtensionName]);
        var requestIds = requests
            .Select(item => item.OperationId)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var receiptIds = receipts
            .Select(item => item.OperationId)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (!evidenceOperationIds.SequenceEqual(
                requestIds,
                StringComparer.Ordinal)
            || !evidenceOperationIds.SequenceEqual(
                receiptIds,
                StringComparer.Ordinal)
            || !evidenceOperationIds.SequenceEqual(
                plan.OperationIds,
                StringComparer.Ordinal)
            || !GameContextAdvancementPlanner.Equivalent(
                plan.Resulting,
                GameContextEnvelope.ValidateForRun(
                    candidateRun,
                    nameof(candidateRun))!))
        {
            throw RecoveryInvalid(
                "A game-context checkpoint is not supported by every "
                + "action receipt in its turn.");
        }
    }

    private static IReadOnlyList<string> ReadOperationIds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw RecoveryInvalid(
                "Game-context source operations must be an array.");
        }

        var result = new List<string>();
        string? previous = null;
        foreach (var item in value.EnumerateArray())
        {
            if (result.Count >= 4_096
                || item.ValueKind != JsonValueKind.String)
            {
                throw RecoveryInvalid(
                    "Game-context source operations are malformed or "
                    + "over capacity.");
            }

            string operationId;
            try
            {
                operationId = RuntimeGuard.RequiredId(
                    item.GetString(),
                    SourceOperationsExtensionName);
            }
            catch (ArgumentException exception)
            {
                throw RecoveryInvalid(
                    "A game-context source operation is invalid.",
                    exception);
            }

            if (previous is not null
                && string.CompareOrdinal(previous, operationId) >= 0)
            {
                throw RecoveryInvalid(
                    "Game-context source operations must be unique and "
                    + "ordinally sorted.");
            }

            result.Add(operationId);
            previous = operationId;
        }

        if (result.Count == 0)
        {
            throw RecoveryInvalid(
                "A game-context checkpoint requires source operations.");
        }

        return result;
    }

    private static GameContextCoordinate ReadRecoveryCoordinate(
        JsonElement value,
        string parameterName)
    {
        try
        {
            return GameContextReceiptEnvelope.ReadCoordinate(
                value,
                parameterName);
        }
        catch (GameContextAdvancementException exception)
        {
            throw RecoveryInvalid(
                "A recovered game-context coordinate is invalid.",
                exception);
        }
    }

    private static InvalidDataException RecoveryInvalid(
        string message,
        Exception? innerException = null)
    {
        return new InvalidDataException(message, innerException);
    }
}
