using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public interface IGameOperationReconciler
{
    ValueTask<ActionReceipt> QueryOperationAsync(
        ActionRequest request,
        CancellationToken cancellationToken);
}

public sealed class RecoveredRun
{
    public AgentRun Run { get; set; } = new();

    public IReadOnlyList<NormalizedMessage> Transcript { get; set; } =
        Array.Empty<NormalizedMessage>();

    public TurnSnapshot? LastTurnSnapshot { get; set; }

    public JsonElement? FinalOutput { get; set; }

    public ProviderOpaqueContinuationState? ProviderOpaqueContinuationState
    {
        get;
        internal set;
    }

    internal string? FinalOutputTurnId { get; set; }

    public IReadOnlyList<OperationLedgerEntry> PendingOperations { get; set; } =
        Array.Empty<OperationLedgerEntry>();

    public IReadOnlyList<RecoveredProviderDispatch>
        UnsettledProviderDispatches
    { get; set; } =
            Array.Empty<RecoveredProviderDispatch>();

    public string? ReplaySafeTurnId { get; set; }

    public IReadOnlyList<ContextCandidate> RecoveryContext { get; set; } =
        Array.Empty<ContextCandidate>();

    public IReadOnlyList<SkillReference> RecoveryActiveSkills { get; set; } =
        Array.Empty<SkillReference>();

    internal IReadOnlyList<SkillActivationStateRecord>
        RecoverySkillActivationState
    { get; set; } = Array.Empty<SkillActivationStateRecord>();

    public string RecoveryWorkloadClass { get; set; } =
        ProviderWorkloadClasses.Interactive;

    public string RecoveryExecutionMode { get; set; } =
        DurableExecutionModes.Agent;

    public ModelInferenceOptions? RecoveryInference { get; set; }

    public ProviderRoutePreference? RecoveryRoutePreference { get; set; }

    public IReadOnlyList<ToolActivationRecord> RecoveryToolActivations
    { get; set; } = Array.Empty<ToolActivationRecord>();

    internal IReadOnlyList<PreparedRuntimeMemoryCommit>
        PendingMemoryCommits
    { get; set; } =
            Array.Empty<PreparedRuntimeMemoryCommit>();

    internal IReadOnlyList<string> CompletedMemoryCommitIds { get; set; } =
        Array.Empty<string>();

    internal IReadOnlyList<RecoveredMemoryReceiptBatch> MemoryReceiptBatches
    { get; set; } = Array.Empty<RecoveredMemoryReceiptBatch>();

    internal IReadOnlyList<ActionRequest> RecoveryActionRequests
    { get; set; } = Array.Empty<ActionRequest>();

    internal IReadOnlyList<string> GameContextAdvancedTurnIds
    { get; set; } = Array.Empty<string>();

    internal IReadOnlyList<string> CommittedProviderResultTurnIds
    { get; set; } = Array.Empty<string>();

    internal int FinalOutputAdmissionAttempts { get; set; }

    internal IReadOnlyDictionary<string, PreparedRuntimeMemoryCommit>
        MemoryCommitRecords
    { get; set; } =
            new ReadOnlyDictionary<string, PreparedRuntimeMemoryCommit>(
                new Dictionary<string, PreparedRuntimeMemoryCommit>(
                    StringComparer.Ordinal));

    internal IReadOnlyDictionary<string, IReadOnlyList<string>>
        CommittedMemorySourceEventIdsByTurn
    { get; set; } =
            new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.Ordinal));

    internal IReadOnlyDictionary<string, NormalizedMessage>
        CommittedAssistantMessagesByTurn
    { get; set; } =
            new ReadOnlyDictionary<string, NormalizedMessage>(
                new Dictionary<string, NormalizedMessage>(
                    StringComparer.Ordinal));

    internal IReadOnlyList<FinalOutputCommittedEvidence>
        FinalOutputCommittedEvidence
    { get; set; } = Array.Empty<FinalOutputCommittedEvidence>();

    internal JsonElement? FinalOutputAdmissionEvidence { get; set; }

    internal DurableTerminalOutcome? TerminalOutcome { get; set; }
}

internal sealed class RecoveredProviderResultIdentity
{
    public RecoveredProviderResultIdentity(
        string providerId,
        string providerAttemptId,
        string streamAttemptId)
    {
        ProviderId = RuntimeGuard.RequiredId(
            providerId,
            nameof(providerId));
        ProviderAttemptId = RuntimeGuard.RequiredId(
            providerAttemptId,
            nameof(providerAttemptId));
        StreamAttemptId = RuntimeGuard.RequiredId(
            streamAttemptId,
            nameof(streamAttemptId));
    }

    public string ProviderId { get; }

    public string ProviderAttemptId { get; }

    public string StreamAttemptId { get; }
}

internal sealed class RecoveredMemoryReceiptBatch
{
    public RecoveredMemoryReceiptBatch(
        string turnId,
        IReadOnlyList<ActionReceipt> receipts,
        IReadOnlyList<string> sourceEventIds)
    {
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        Receipts = new ReadOnlyCollection<ActionReceipt>(
            receipts
                .Select(
                    item => ProtocolJson.DeserializeActionReceipt(
                        ProtocolJson.Serialize(item)))
                .OrderBy(item => item.OperationId, StringComparer.Ordinal)
                .ToArray());
        SourceEventIds = new ReadOnlyCollection<string>(
            sourceEventIds
                .Select(
                    item => RuntimeGuard.RequiredUtf8(
                        item,
                        128,
                        nameof(sourceEventIds)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray());
        if (SourceEventIds.Count == 0)
        {
            throw new InvalidDataException(
                "A recovered terminal receipt batch requires source events.");
        }
    }

    public string TurnId { get; }

    public IReadOnlyList<ActionReceipt> Receipts { get; }

    public IReadOnlyList<string> SourceEventIds { get; }
}

public sealed class RecoveredProviderDispatch
{
    public string ProviderId { get; set; } = string.Empty;

    public string? ModelId { get; set; }

    public string? TransportDialect { get; set; }

    public string? ProviderCapabilityDigest { get; set; }

    public string? ProviderRouteDigest { get; set; }

    public string? ProviderRoutePolicyVersion { get; set; }

    public string? ProviderRoutePolicyDigest { get; set; }

    public string? ProviderDialectSemanticDigest { get; set; }

    public ProviderDialectContract? ProviderDialectContract { get; set; }

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public bool UsageSettled { get; set; }
}

internal sealed class ReconciliationQueryRegistry
{
    internal const int DefaultCapacity = 64;

    private readonly ConcurrentDictionary<string, byte> _active =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _capacity;

    public ReconciliationQueryRegistry(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = new SemaphoreSlim(capacity, capacity);
    }

    public static ReconciliationQueryRegistry Shared { get; } = new();

    internal int ActiveCount => _active.Count;

    public bool TryAcquire(
        ActionRequest request,
        out ReconciliationQueryLease? lease)
    {
        if (!_capacity.Wait(0))
        {
            lease = null;
            return false;
        }

        var key = request.WorldId
                  + "\0"
                  + request.RunId
                  + "\0"
                  + request.OperationId;
        if (!_active.TryAdd(key, 0))
        {
            _capacity.Release();
            lease = null;
            return false;
        }

        lease = new ReconciliationQueryLease(this, key);
        return true;
    }

    private void Release(string key)
    {
        if (_active.TryRemove(key, out _))
        {
            _capacity.Release();
        }
    }

    internal sealed class ReconciliationQueryLease : IDisposable
    {
        private ReconciliationQueryRegistry? _owner;
        private readonly string _key;

        public ReconciliationQueryLease(
            ReconciliationQueryRegistry owner,
            string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_key);
        }
    }
}

public sealed class RunRecovery
{
    internal const string ResultSchemaExtension = "resultSchema";
    internal const string ReplaySafeTurnAbandonedReason =
        "provider_safe_turn_abandoned";

    private readonly IDurableSessionStore _store;
    private readonly IOperationLedger _operations;
    private readonly JournalCoordinator _journal;
    private readonly ReconciliationQueryRegistry _reconciliationQueries;
    private readonly int _maxEventsPerRun;
    private readonly int _maxEventUtf8Bytes;
    private readonly int _maxAggregateEventUtf8Bytes;

    public RunRecovery(
        IDurableSessionStore store,
        IOperationLedger operations,
        JournalCoordinator journal)
        : this(
            store,
            operations,
            journal,
            ReconciliationQueryRegistry.Shared,
            options: null)
    {
    }

    public RunRecovery(
        IDurableSessionStore store,
        IOperationLedger operations,
        JournalCoordinator journal,
        RunRecoveryOptions options)
        : this(
            store,
            operations,
            journal,
            ReconciliationQueryRegistry.Shared,
            options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    internal RunRecovery(
        IDurableSessionStore store,
        IOperationLedger operations,
        JournalCoordinator journal,
        ReconciliationQueryRegistry reconciliationQueries)
        : this(
            store,
            operations,
            journal,
            reconciliationQueries,
            options: null)
    {
    }

    internal RunRecovery(
        IDurableSessionStore store,
        IOperationLedger operations,
        JournalCoordinator journal,
        ReconciliationQueryRegistry reconciliationQueries,
        RunRecoveryOptions? options)
    {
        options ??= new RunRecoveryOptions();
        if (options.MaxEventsPerRun <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxEventsPerRun must be positive.");
        }

        if (options.MaxAggregateEventUtf8Bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxAggregateEventUtf8Bytes must be positive.");
        }

        if (options.MaxEventUtf8Bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxEventUtf8Bytes must be positive.");
        }

        _store = store;
        _operations = operations;
        _journal = journal;
        _reconciliationQueries = reconciliationQueries
                                 ?? throw new ArgumentNullException(
                                     nameof(reconciliationQueries));
        _maxEventsPerRun = options.MaxEventsPerRun;
        _maxEventUtf8Bytes = options.MaxEventUtf8Bytes;
        _maxAggregateEventUtf8Bytes =
            options.MaxAggregateEventUtf8Bytes;
    }

    public async ValueTask<RecoveredRun?> LoadAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var events = await _store.ReadRunAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (events is null)
        {
            throw new InvalidDataException(
                "The journal returned a null runtime event collection.");
        }

        var orderedEvents = ValidateRecoveryEvents(
            runId,
            events,
            _maxEventsPerRun,
            _maxEventUtf8Bytes,
            _maxAggregateEventUtf8Bytes);
        if (orderedEvents.Count == 0)
        {
            return null;
        }

        AgentRun? run = null;
        TurnSnapshot? lastSnapshot = null;
        ToolDisclosureJournalRecord? lastToolDisclosure = null;
        DurableRunInputSnapshot? initialInput = null;
        JsonElement? finalOutput = null;
        JsonElement? finalOutputAdmissionEvidence = null;
        JsonElement? finalOutputPresentation = null;
        string? finalOutputTurnId = null;
        var transcript = new List<NormalizedMessage>();
        var transcriptTurnIds = new Dictionary<string, string?>(
            StringComparer.Ordinal);
        var messagesById = new Dictionary<string, NormalizedMessage>(
            StringComparer.Ordinal);
        var assistantMessagesByTurn =
            new Dictionary<string, NormalizedMessage>(StringComparer.Ordinal);
        var assistantTranscriptAttemptsByTurn =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var assistantTranscriptProvidersByTurn =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var committedProviderResultsByTurn =
            new Dictionary<string, RecoveredProviderResultIdentity>(
                StringComparer.Ordinal);
        var providerPresentationsByTurn =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        ProviderOpaqueContinuationState? providerOpaqueContinuationState =
            null;
        ProviderCacheKey? previousProviderCacheKey = null;
        var memorySourceEventIdsByTurn =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var journalMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var requests = new Dictionary<string, ActionRequest>(
            StringComparer.Ordinal);
        var requestEventSequences = new Dictionary<string, long>(
            StringComparer.Ordinal);
        var terminalReceipts = new Dictionary<string, RecoveredReceipt>(
            StringComparer.Ordinal);
        var terminalReceiptEvents = new HashSet<string>(StringComparer.Ordinal);
        var terminalReceiptSourceEventIds =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var latestJournalReceipts =
            new Dictionary<string, ActionReceipt>(StringComparer.Ordinal);
        var latestJournalReceiptSequences =
            new Dictionary<string, long>(StringComparer.Ordinal);
        var uncertainActionOperationIds =
            new HashSet<string>(StringComparer.Ordinal);
        var assistantToolCalls = new Dictionary<string, RecoveredToolCall>(
            StringComparer.Ordinal);
        var completedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var durableRequestCallIds = new HashSet<string>(StringComparer.Ordinal);
        var unsettledProviderDispatches =
            new Dictionary<string, RecoveredProviderDispatch>(
                StringComparer.Ordinal);
        var providerUnsafeTurnIds = new HashSet<string>(
            StringComparer.Ordinal);
        var startedTurnIds = new HashSet<string>(StringComparer.Ordinal);
        var abandonedPreProviderTurnIds = new HashSet<string>(
            StringComparer.Ordinal);
        var preparedMemoryCommits =
            new Dictionary<string, PreparedRuntimeMemoryCommit>(
                StringComparer.Ordinal);
        var completedMemoryCommitIds = new HashSet<string>(
            StringComparer.Ordinal);
        var committedProviderResultTurnIds = new HashSet<string>(
            StringComparer.Ordinal);
        var finalOutputAdmissionAttempts = 0;
        DurableTerminalOutcome? terminalOutcome = null;
        var gameContextAdvancedTurnIds = new HashSet<string>(
            StringComparer.Ordinal);
        var actionCount = 0;
        var turnCount = 0;
        foreach (var runtimeEvent in orderedEvents)
        {
            if (runtimeEvent.Extensions.TryGetValue(
                    TerminalOutcomeJournalCodec.ExtensionName,
                    out var terminalOutcomeEvidence))
            {
                if (terminalOutcome is not null
                    || !string.Equals(
                        runtimeEvent.Durability,
                        EventDurabilities.Durable,
                        StringComparison.Ordinal)
                    || runtimeEvent.Kind is not RuntimeEventKinds.RunFailed
                        and not RuntimeEventKinds.RunCancelled
                        and not RuntimeEventKinds.ActionReconciling
                        and not RuntimeEventKinds.RunCheckpoint)
                {
                    throw new InvalidDataException(
                        "The journal contains invalid terminal-outcome "
                        + "metadata.");
                }

                terminalOutcome = TerminalOutcomeJournalCodec.Read(
                    terminalOutcomeEvidence);
            }

            RecoveredProviderDispatch? settledProviderDispatch = null;
            var previousRunCheckpoint = run;
            if (RunCheckpointLifecycleValidator.IsCheckpointKind(
                    runtimeEvent.Kind))
            {
                run = RunCheckpointLifecycleValidator.ValidateAndClone(
                    runtimeEvent,
                    run,
                    runtimeEvent.Sequence,
                    checked(runtimeEvent.Sequence + 1));
            }

            if (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.GameContextAdvanced,
                    StringComparison.Ordinal))
            {
                if (previousRunCheckpoint is null
                    || run is null
                    || string.IsNullOrWhiteSpace(runtimeEvent.TurnId)
                    || !gameContextAdvancedTurnIds.Add(
                        runtimeEvent.TurnId))
                {
                    throw new InvalidDataException(
                        "A game-context checkpoint has no active run turn.");
                }

                var advancementRequests = requests.Values
                    .Where(
                        item => string.Equals(
                            item.TurnId,
                            runtimeEvent.TurnId,
                            StringComparison.Ordinal))
                    .OrderBy(
                        item => item.OperationId,
                        StringComparer.Ordinal)
                    .ToArray();
                var advancementReceipts = terminalReceipts.Values
                    .Where(
                        item => string.Equals(
                            item.TurnId,
                            runtimeEvent.TurnId,
                            StringComparison.Ordinal))
                    .Select(item => item.Receipt)
                    .OrderBy(
                        item => item.OperationId,
                        StringComparer.Ordinal)
                    .ToArray();
                GameContextAdvancementJournalCodec
                    .ValidateReceiptEvidence(
                        runtimeEvent,
                        previousRunCheckpoint,
                        run,
                        advancementRequests,
                        advancementReceipts);
            }

            ValidateProviderCacheUsageEvidence(runtimeEvent);

            if (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ProviderDispatchStarted,
                    StringComparison.Ordinal))
            {
                var dispatch = ReadProviderDispatch(runtimeEvent);
                if (!unsettledProviderDispatches.TryAdd(
                        dispatch.StreamAttemptId,
                        dispatch))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate provider dispatch "
                        + "identities.");
                }
            }
            else if (IsProviderDispatchSettlement(runtimeEvent.Kind)
                     && !string.IsNullOrWhiteSpace(runtimeEvent.StreamAttemptId))
            {
                if (unsettledProviderDispatches.TryGetValue(
                        runtimeEvent.StreamAttemptId,
                        out var dispatch)
                    && !string.Equals(
                        runtimeEvent.Kind,
                        RuntimeEventKinds.ProviderDispatchKnownZero,
                        StringComparison.Ordinal))
                {
                    providerUnsafeTurnIds.Add(dispatch.TurnId);
                }

                settledProviderDispatch = ApplyProviderDispatchSettlement(
                    unsettledProviderDispatches,
                    runtimeEvent);
            }

            if (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ProviderResultCommitted,
                    StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(runtimeEvent.TurnId)
                    || string.IsNullOrWhiteSpace(runtimeEvent.ProviderId)
                    || string.IsNullOrWhiteSpace(runtimeEvent.AttemptId)
                    || string.IsNullOrWhiteSpace(
                        runtimeEvent.StreamAttemptId)
                    || settledProviderDispatch is null
                    || !assistantMessagesByTurn.ContainsKey(
                        runtimeEvent.TurnId)
                    || !assistantTranscriptAttemptsByTurn.TryGetValue(
                        runtimeEvent.TurnId,
                        out var transcriptAttemptId)
                    || !assistantTranscriptProvidersByTurn.TryGetValue(
                        runtimeEvent.TurnId,
                        out var transcriptProviderId)
                    || !string.Equals(
                        transcriptAttemptId,
                        runtimeEvent.AttemptId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        transcriptProviderId,
                        runtimeEvent.ProviderId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A committed provider result has no matching "
                        + "provider dispatch and assistant transcript.");
                }

                if (!committedProviderResultTurnIds.Add(runtimeEvent.TurnId))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate committed provider "
                        + "results for one turn.");
                }

                ValidateProviderResultRoute(
                    runtimeEvent,
                    settledProviderDispatch);
                if (!committedProviderResultsByTurn.TryAdd(
                        runtimeEvent.TurnId,
                        new RecoveredProviderResultIdentity(
                            runtimeEvent.ProviderId,
                            runtimeEvent.AttemptId,
                            runtimeEvent.StreamAttemptId)))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate committed provider "
                        + "result identities.");
                }

                var strictAdmission = FinalOutputAdmissionBinding.Read(
                    RequireRunForReceipt(run));
                var hasPresentation =
                    runtimeEvent.Extensions.TryGetValue(
                        FinalOutputAdmissionControl
                            .PresentationExtensionName,
                        out var presentation);
                if (strictAdmission is null)
                {
                    if (hasPresentation)
                    {
                        throw new InvalidDataException(
                            "A non-strict provider result contains "
                            + "final-output presentation metadata.");
                    }
                }
                else
                {
                    if (!hasPresentation)
                    {
                        throw new InvalidDataException(
                            "A strict provider result has no final-output "
                            + "presentation metadata.");
                    }

                    if (FinalOutputAdmissionCodec.IsCommittedAttempt(
                            assistantMessagesByTurn[runtimeEvent.TurnId],
                            presentation))
                    {
                        finalOutputAdmissionAttempts = checked(
                            finalOutputAdmissionAttempts + 1);
                    }

                    if (!providerPresentationsByTurn.TryAdd(
                            runtimeEvent.TurnId,
                            presentation.Clone()))
                    {
                        throw new InvalidDataException(
                            "The journal contains duplicate final-output "
                            + "presentation metadata.");
                    }
                }

                providerOpaqueContinuationState = null;
                if (runtimeEvent.Extensions.TryGetValue(
                        ProviderOpaqueContinuationState.JournalExtensionName,
                        out var opaqueEnvelope))
                {
                    if (string.IsNullOrWhiteSpace(
                            runtimeEvent.ProviderRouteDigest)
                        || settledProviderDispatch
                                .ProviderDialectContract
                                ?.OpaqueContinuationStateVersion
                            is not string opaqueStateVersion)
                    {
                        throw new InvalidDataException(
                            "A durable provider continuation has no result "
                            + "route and dialect binding.");
                    }

                    try
                    {
                        providerOpaqueContinuationState =
                            ProviderOpaqueContinuationState
                                .RestoreDurable(
                                    opaqueEnvelope,
                                    runtimeEvent.ProviderId,
                                    runtimeEvent.ProviderRouteDigest,
                                    opaqueStateVersion);
                    }
                    catch (
                        ProviderOpaqueContinuationStateException exception)
                    {
                        throw new InvalidDataException(
                            "A durable provider continuation is invalid.",
                            exception);
                    }
                }

                AddMemorySource(
                    memorySourceEventIdsByTurn,
                    runtimeEvent.TurnId,
                    runtimeEvent.EventId);
            }

            if (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.TranscriptMessage,
                    StringComparison.Ordinal))
            {
                var message = NormalizedMessageJournalCodec.Decode(
                    runtimeEvent.Payload);
                if (!journalMessageIds.Add(message.MessageId))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate transcript message ids.");
                }

                if (messagesById.TryGetValue(
                        message.MessageId,
                        out var existingMessage))
                {
                    if (!MessagesAreEquivalent(existingMessage, message))
                    {
                        throw new InvalidDataException(
                            "A transcript message conflicts with a recovered "
                            + "authoritative tool result.");
                    }
                }
                else
                {
                    messagesById.Add(message.MessageId, message);
                    transcript.Add(message);
                    transcriptTurnIds[message.MessageId] =
                        runtimeEvent.TurnId;
                }

                if (string.Equals(
                        message.Role,
                        NormalizedRoles.Assistant,
                        StringComparison.Ordinal))
                {
                    var isInitialHistory =
                        string.Equals(
                            runtimeEvent.TurnId,
                            "initial",
                            StringComparison.Ordinal)
                        && runtimeEvent.AttemptId is null
                        && runtimeEvent.StreamAttemptId is null
                        && runtimeEvent.ProviderId is null;
                    if (!isInitialHistory
                        && (string.IsNullOrWhiteSpace(runtimeEvent.TurnId)
                            || string.IsNullOrWhiteSpace(
                                runtimeEvent.AttemptId)
                            || string.IsNullOrWhiteSpace(
                                runtimeEvent.ProviderId)
                            || !assistantMessagesByTurn.TryAdd(
                                runtimeEvent.TurnId,
                                message)
                            || !assistantTranscriptAttemptsByTurn.TryAdd(
                                runtimeEvent.TurnId,
                                runtimeEvent.AttemptId)
                            || !assistantTranscriptProvidersByTurn.TryAdd(
                                runtimeEvent.TurnId,
                                runtimeEvent.ProviderId)))
                    {
                        throw new InvalidDataException(
                            "An assistant transcript has no unique turn and "
                            + "provider attempt.");
                    }
                }

                foreach (var part in message.Parts)
                {
                    if (string.Equals(
                            part.Type,
                            NormalizedPartTypes.ToolCall,
                            StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(part.ToolCallId)
                        && !string.IsNullOrWhiteSpace(part.ToolName))
                    {
                        if (!assistantToolCalls.TryAdd(
                                part.ToolCallId,
                                new RecoveredToolCall(
                                    part.ToolCallId,
                                    part.ToolName,
                                    runtimeEvent.TurnId ?? "recovered-turn",
                                    runtimeEvent.AttemptId,
                                    runtimeEvent.Timestamp)))
                        {
                            throw new InvalidDataException(
                                "The journal contains duplicate assistant "
                                + "tool-call ids.");
                        }
                    }
                    else if (string.Equals(
                                 part.Type,
                                 NormalizedPartTypes.ToolResult,
                                 StringComparison.Ordinal)
                             && !string.IsNullOrWhiteSpace(part.ToolCallId))
                    {
                        completedToolCallIds.Add(part.ToolCallId);
                    }
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.RunInputCaptured,
                         StringComparison.Ordinal))
            {
                if (initialInput is not null)
                {
                    throw new InvalidDataException(
                        "The journal contains more than one initial "
                        + "run-input snapshot.");
                }

                initialInput = DurableRunInputJournalCodec.Decode(
                    runtimeEvent.Payload);
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.TurnSnapshot,
                         StringComparison.Ordinal))
            {
                var snapshot = ProtocolJson.DeserializeTurnSnapshot(
                    runtimeEvent.Payload.GetRawText());
                ProtocolValidator.EnsureValid(snapshot);
                if (!string.Equals(
                        snapshot.RunId,
                        runId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        snapshot.TurnId,
                        runtimeEvent.TurnId,
                        StringComparison.Ordinal)
                    || snapshot.RuntimeGeneration
                    != runtimeEvent.RuntimeGeneration)
                {
                    throw new InvalidDataException(
                        "A recovered turn snapshot does not match "
                        + "its enclosing runtime event.");
                }

                previousProviderCacheKey =
                    ValidateRecoveredTurnSnapshotExtensions(
                        snapshot,
                        previousProviderCacheKey,
                        ReadActivatedSkillState(
                            transcript,
                            transcriptTurnIds,
                            snapshot.TurnId));
                lastSnapshot = snapshot;
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ToolDisclosureChanged,
                         StringComparison.Ordinal))
            {
                lastToolDisclosure = ToolDisclosureJournalCodec.Decode(
                    runtimeEvent.Payload,
                    maximumActivations: 128);
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ActionRequested,
                         StringComparison.Ordinal))
            {
                actionCount++;
                var request = ProtocolJson.DeserializeActionRequest(
                    runtimeEvent.Payload.GetRawText());
                ProtocolValidator.EnsureValid(request);
                var requestRun = RequireRunForReceipt(run);
                if (!string.Equals(
                        request.RunId,
                        runId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        request.TurnId,
                        runtimeEvent.TurnId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        request.AgentId,
                        requestRun.AgentId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        request.WorldId,
                        requestRun.WorldId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A recovered action request does not match "
                        + "its enclosing runtime event.");
                }

                if (!requests.TryAdd(request.OperationId, request))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate action operation ids.");
                }
                requestEventSequences.Add(
                    request.OperationId,
                    runtimeEvent.Sequence);
                if (!durableRequestCallIds.Add(request.ToolCallId))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate action-request "
                        + "tool-call ids.");
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ActionOutcomeUncertain,
                         StringComparison.Ordinal))
            {
                var parsedUncertainty =
                    ProtocolJson.DeserializeActionReceipt(
                        runtimeEvent.Payload.GetRawText());
                if (!requests.TryGetValue(
                        parsedUncertainty.OperationId,
                        out var request))
                {
                    throw new InvalidDataException(
                        "A recovered action uncertainty has no preceding "
                        + "action request.");
                }

                EnsureReceiptEventMatchesRequest(runtimeEvent, request);
                var uncertainty =
                    ActionReceiptIngressValidator.ValidateAndClone(
                        request,
                        parsedUncertainty,
                        RequireRunForReceipt(run));
                if (!string.Equals(
                        uncertainty.Status,
                        ReceiptStatuses.Unknown,
                        StringComparison.Ordinal)
                    || !uncertainActionOperationIds.Add(
                        uncertainty.OperationId)
                    || latestJournalReceipts.ContainsKey(
                        uncertainty.OperationId))
                {
                    throw new InvalidDataException(
                        "The journal contains an invalid action uncertainty.");
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ActionReceived,
                         StringComparison.Ordinal))
            {
                var parsedReceipt = ProtocolJson.DeserializeActionReceipt(
                    runtimeEvent.Payload.GetRawText());
                if (!requests.TryGetValue(
                        parsedReceipt.OperationId,
                        out var request))
                {
                    throw new InvalidDataException(
                        "A recovered receipt has no preceding action request.");
                }

                EnsureReceiptEventMatchesRequest(runtimeEvent, request);
                var receipt = ActionReceiptIngressValidator.ValidateAndClone(
                    request,
                    parsedReceipt,
                    RequireRunForReceipt(run));
                if (latestJournalReceipts.TryGetValue(
                        receipt.OperationId,
                        out var previousReceipt)
                    && receipt.Revision <= previousReceipt.Revision)
                {
                    throw new InvalidDataException(
                        "Recovered action receipt revisions are not "
                        + "strictly increasing.");
                }

                latestJournalReceipts[receipt.OperationId] = receipt;
                latestJournalReceiptSequences[receipt.OperationId] =
                    runtimeEvent.Sequence;
                if (!string.Equals(
                        receipt.Status,
                        ReceiptStatuses.Unknown,
                        StringComparison.Ordinal))
                {
                    if (!terminalReceipts.TryAdd(
                            ReceiptKey(receipt),
                            new RecoveredReceipt(
                                receipt,
                                request.TurnId,
                                runtimeEvent.AttemptId)))
                    {
                        throw new InvalidDataException(
                            "The journal contains duplicate terminal "
                            + "action receipts.");
                    }

                    AddMemorySource(
                        memorySourceEventIdsByTurn,
                        request.TurnId,
                        runtimeEvent.EventId);
                    terminalReceiptSourceEventIds.Add(
                        ReceiptKey(receipt),
                        runtimeEvent.EventId);
                    var message = NormalizedTranscript.ToolResult(
                        ToolResultMessageId(receipt),
                        request.ToolCallId,
                        request.ActionName,
                        FinalOutputAdmissionBinding.Read(
                                RequireRunForReceipt(run)) is null
                            ? ProtocolJson.ToElement(receipt)
                            : FinalOutputAdmissionCodec
                                .ForModelPresentation(
                                    receipt,
                                    runtimeEvent.EventId),
                        receipt.ReceivedAt);
                    if (messagesById.TryGetValue(
                            message.MessageId,
                            out var existingMessage))
                    {
                        if (!MessagesAreEquivalent(existingMessage, message))
                        {
                            throw new InvalidDataException(
                                "A recovered tool-result message conflicts "
                                + "with its authoritative receipt.");
                        }
                    }
                    else
                    {
                        messagesById.Add(message.MessageId, message);
                        transcript.Add(message);
                        transcriptTurnIds[message.MessageId] =
                            request.TurnId;
                    }

                    completedToolCallIds.Add(request.ToolCallId);
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ToolCompleted,
                         StringComparison.Ordinal)
                     || string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ToolFailed,
                         StringComparison.Ordinal))
            {
                var parsedReceipt = ProtocolJson.DeserializeActionReceipt(
                    runtimeEvent.Payload.GetRawText());
                if (!requests.TryGetValue(
                        parsedReceipt.OperationId,
                        out var request))
                {
                    throw new InvalidDataException(
                        "A recovered terminal receipt event has no "
                        + "preceding action request.");
                }

                EnsureReceiptEventMatchesRequest(runtimeEvent, request);
                var receipt = ActionReceiptIngressValidator.ValidateAndClone(
                    request,
                    parsedReceipt,
                    RequireRunForReceipt(run));
                var receiptKey = ReceiptKey(receipt);
                if (!terminalReceipts.TryGetValue(
                        receiptKey,
                        out var receivedReceipt)
                    || !ReceiptsAreEquivalent(
                        receipt,
                        receivedReceipt.Receipt))
                {
                    throw new InvalidDataException(
                        "A terminal receipt event does not match a preceding "
                        + "authoritative receipt.");
                }

                var kindMatchesStatus =
                    string.Equals(
                        runtimeEvent.Kind,
                        RuntimeEventKinds.ToolFailed,
                        StringComparison.Ordinal)
                        ? string.Equals(
                            receipt.Status,
                            ReceiptStatuses.Failed,
                            StringComparison.Ordinal)
                        : string.Equals(
                              receipt.Status,
                              ReceiptStatuses.Succeeded,
                              StringComparison.Ordinal)
                          || string.Equals(
                              receipt.Status,
                              ReceiptStatuses.Rejected,
                              StringComparison.Ordinal);
                if (!kindMatchesStatus)
                {
                    throw new InvalidDataException(
                        "A terminal receipt status does not match its "
                        + "runtime event kind.");
                }

                if (!terminalReceiptEvents.Add(receiptKey))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate terminal receipt "
                        + "events.");
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.MemoryCommitPrepared,
                         StringComparison.Ordinal))
            {
                var memoryRun = RequireMemoryOutboxPosition(
                    runtimeEvent,
                    run,
                    lastSnapshot,
                    startedTurnIds,
                    allowAfterTerminal: false);
                var sourceIds = RequireMemorySources(
                    memorySourceEventIdsByTurn,
                    terminalReceipts.Values,
                    terminalReceiptSourceEventIds,
                    runtimeEvent.TurnId!);
                var expectedCommitId = RuntimeMemoryAgentLoop.CommitId(
                    runId,
                    runtimeEvent.RuntimeGeneration,
                    runtimeEvent.TurnId!);
                var prepared =
                    RuntimeMemoryCommitJournalCodec.DecodePrepared(
                        runtimeEvent.Payload,
                        runtimeEvent.TurnId!,
                        expectedCommitId);
                ValidateRecoveredMemoryCommit(
                    prepared,
                    memoryRun,
                    sourceIds,
                    lastSnapshot!);
                EnsureDerivedEventId(
                    runtimeEvent,
                    runId,
                    "memory-prepared:" + prepared.CommitId);
                if (!preparedMemoryCommits.TryAdd(
                        prepared.CommitId,
                        prepared))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate prepared memory "
                        + "commits.");
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.MemoryCommitCompleted,
                         StringComparison.Ordinal))
            {
                _ = RequireMemoryOutboxPosition(
                    runtimeEvent,
                    run,
                    lastSnapshot,
                    startedTurnIds,
                    allowAfterTerminal: true);
                var expectedCommitId = RuntimeMemoryAgentLoop.CommitId(
                    runId,
                    runtimeEvent.RuntimeGeneration,
                    runtimeEvent.TurnId!);
                var commitId =
                    RuntimeMemoryCommitJournalCodec.DecodeCompleted(
                        runtimeEvent.Payload,
                        runtimeEvent.TurnId!,
                        expectedCommitId);
                EnsureDerivedEventId(
                    runtimeEvent,
                    runId,
                    "memory-completed:" + commitId);
                if (!preparedMemoryCommits.ContainsKey(commitId)
                    || !completedMemoryCommitIds.Add(commitId))
                {
                    throw new InvalidDataException(
                        "A memory completion has no unique preceding "
                        + "prepared commit.");
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.MemoryCommitSettled,
                         StringComparison.Ordinal))
            {
                var memoryRun = RequireMemoryOutboxPosition(
                    runtimeEvent,
                    run,
                    lastSnapshot,
                    startedTurnIds,
                    allowAfterTerminal: false);
                var sourceIds = RequireMemorySources(
                    memorySourceEventIdsByTurn,
                    terminalReceipts.Values,
                    terminalReceiptSourceEventIds,
                    runtimeEvent.TurnId!);
                var expectedCommitId = RuntimeMemoryAgentLoop.CommitId(
                    runId,
                    runtimeEvent.RuntimeGeneration,
                    runtimeEvent.TurnId!);
                var settled =
                    RuntimeMemoryCommitJournalCodec.DecodeSettled(
                        runtimeEvent.Payload,
                        runtimeEvent.TurnId!,
                        expectedCommitId);
                ValidateRecoveredMemoryCommit(
                    settled,
                    memoryRun,
                    sourceIds,
                    lastSnapshot!);
                EnsureDerivedEventId(
                    runtimeEvent,
                    runId,
                    "memory-settled:" + settled.CommitId);
                if (!preparedMemoryCommits.TryAdd(
                        settled.CommitId,
                        settled)
                    || !completedMemoryCommitIds.Add(settled.CommitId))
                {
                    throw new InvalidDataException(
                        "The journal contains a duplicate memory settlement.");
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.TurnCompleted,
                         StringComparison.Ordinal)
                     && string.Equals(
                         runtimeEvent.ReasonCode,
                         ReplaySafeTurnAbandonedReason,
                         StringComparison.Ordinal)
                     && !string.IsNullOrWhiteSpace(runtimeEvent.TurnId))
            {
                abandonedPreProviderTurnIds.Add(runtimeEvent.TurnId);
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.TurnStarted,
                         StringComparison.Ordinal))
            {
                turnCount++;
                if (!string.IsNullOrWhiteSpace(runtimeEvent.TurnId))
                {
                    startedTurnIds.Add(runtimeEvent.TurnId);
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.AssistantCompleted,
                         StringComparison.Ordinal))
            {
                RecoveredProviderResultIdentity? providerResult = null;
                var hasProviderResult =
                    !string.IsNullOrWhiteSpace(runtimeEvent.TurnId)
                    && committedProviderResultsByTurn.TryGetValue(
                        runtimeEvent.TurnId,
                        out providerResult);
                var hasRuntimeCompletionEvidence =
                    runtimeEvent.Extensions.TryGetValue(
                        RuntimeCompletionEvidence.ExtensionName,
                        out var runtimeCompletionEvidence);
                if (finalOutput.HasValue
                    || string.IsNullOrWhiteSpace(runtimeEvent.TurnId)
                    || hasProviderResult == hasRuntimeCompletionEvidence)
                {
                    throw new InvalidDataException(
                        "An assistant completion has no unique trusted "
                        + "completion source.");
                }

                if (hasProviderResult)
                {
                    if (!assistantMessagesByTurn.ContainsKey(
                            runtimeEvent.TurnId)
                        || !string.Equals(
                            runtimeEvent.ProviderId,
                            providerResult!.ProviderId,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            runtimeEvent.AttemptId,
                            providerResult.ProviderAttemptId,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            runtimeEvent.StreamAttemptId,
                            providerResult.StreamAttemptId,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "An assistant completion does not match its "
                            + "provider result identity.");
                    }
                }
                else
                {
                    RuntimeCompletionEvidence.Validate(
                        runtimeCompletionEvidence,
                        runtimeEvent);
                }

                finalOutput = runtimeEvent.Payload.Clone();
                finalOutputTurnId = runtimeEvent.TurnId;
                if (runtimeEvent.Extensions.TryGetValue(
                        FinalOutputAdmissionCodec.EvidenceExtensionName,
                        out var admissionEvidence))
                {
                    if (finalOutputAdmissionEvidence.HasValue)
                    {
                        throw new InvalidDataException(
                            "The journal contains duplicate final-output "
                            + "admission evidence.");
                    }

                    finalOutputAdmissionEvidence =
                        admissionEvidence.Clone();
                }
                if (runtimeEvent.Extensions.TryGetValue(
                        FinalOutputAdmissionControl
                            .PresentationExtensionName,
                        out var presentation))
                {
                    if (finalOutputPresentation.HasValue)
                    {
                        throw new InvalidDataException(
                            "The journal contains duplicate final-output "
                            + "completion presentation metadata.");
                    }

                    finalOutputPresentation = presentation.Clone();
                }
                AddMemorySource(
                    memorySourceEventIdsByTurn,
                    runtimeEvent.TurnId,
                    runtimeEvent.EventId);
            }
        }

        if (run is null)
        {
            throw new InvalidDataException(
                $"Run '{runId}' has journal entries but no recoverable checkpoint.");
        }

        var cursor = await _store.GetRunCursorAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        ValidateCursor(runId, orderedEvents.Count, cursor);
        run.Revision = cursor.Revision;
        run.Usage.Actions = Math.Max(run.Usage.Actions, actionCount);
        var abandonedStartedTurns = abandonedPreProviderTurnIds.Count(
            startedTurnIds.Contains);
        run.Usage.Turns = Math.Max(
            run.Usage.Turns,
            Math.Max(0, turnCount - abandonedStartedTurns));

        var pendingCandidate = await _operations.ReadPendingOperationsAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        var pending = ValidatePendingOperations(
            run,
            pendingCandidate,
            requests,
            requestEventSequences,
            latestJournalReceipts,
            latestJournalReceiptSequences,
            cursor);
        run.PendingOperationIds = pending
            .Select(item => item.OperationId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        if (run.PendingOperationIds.Count > 0
            && run.State != RunStates.Reconciling)
        {
            run.State = RunStates.Reconciling;
        }

        string? replaySafeTurnId = null;
        if (string.Equals(
                run.State,
                RunStates.Running,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(run.CurrentTurnId)
            && startedTurnIds.Contains(run.CurrentTurnId)
            && !abandonedPreProviderTurnIds.Contains(run.CurrentTurnId)
            && !providerUnsafeTurnIds.Contains(run.CurrentTurnId)
            && !unsettledProviderDispatches.Values.Any(
                dispatch => string.Equals(
                    dispatch.TurnId,
                    run.CurrentTurnId,
                    StringComparison.Ordinal)))
        {
            replaySafeTurnId = run.CurrentTurnId;
        }

        var abandonedTurnIds = new HashSet<string>(
            abandonedPreProviderTurnIds,
            StringComparer.Ordinal);
        if (replaySafeTurnId is not null)
        {
            abandonedTurnIds.Add(replaySafeTurnId);
        }

        transcript.RemoveAll(
            message => transcriptTurnIds.TryGetValue(
                           message.MessageId,
                           out var turnId)
                       && turnId is not null
                       && abandonedTurnIds.Contains(turnId)
                       && IsTurnOutputMessage(message));
        foreach (var toolCallId in assistantToolCalls
                     .Where(
                         pair => abandonedTurnIds.Contains(
                             pair.Value.TurnId))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            assistantToolCalls.Remove(toolCallId);
        }

        IReadOnlyList<ContextCandidate> recoveryContext =
            Array.Empty<ContextCandidate>();
        IReadOnlyList<SkillReference> recoveryActiveSkills =
            Array.Empty<SkillReference>();
        IReadOnlyList<SkillActivationStateRecord> recoverySkillState =
            Array.Empty<SkillActivationStateRecord>();
        var checkpointSkillState = SkillActivationStateCodec.TryRead(
            run,
            DurableRunInputJournalCodec.MaxActiveSkills);
        if (replaySafeTurnId is not null)
        {
            recoverySkillState = ReadActivatedSkillState(
                transcript,
                transcriptTurnIds,
                replaySafeTurnId);
            recoveryActiveSkills = recoverySkillState
                .Select(value => value.ToReference())
                .ToArray();
        }
        else if (lastSnapshot is not null)
        {
            recoverySkillState = ReadActivatedSkillState(
                transcript,
                transcriptTurnIds,
                lastSnapshot.TurnId);
            recoveryActiveSkills = recoverySkillState
                .Select(value => value.ToReference())
                .ToArray();
        }
        else if (lastSnapshot is null && initialInput is not null)
        {
            recoveryContext = initialInput.Context;
            if (checkpointSkillState is not null)
            {
                recoverySkillState = checkpointSkillState;
                recoveryActiveSkills = recoverySkillState
                    .Select(value => value.ToReference())
                    .ToArray();
                if (!SkillReferencesEquivalent(
                        recoveryActiveSkills,
                        initialInput.ActiveSkills))
                {
                    throw new InvalidDataException(
                        "The initial active-skill input does not match "
                        + "the exact run checkpoint.");
                }
            }
            else
            {
                recoveryActiveSkills = initialInput.ActiveSkills;
            }
        }

        if (checkpointSkillState is not null
            && !SkillStatesEquivalent(
                checkpointSkillState,
                recoverySkillState))
        {
            throw new InvalidDataException(
                "The durable active-skill checkpoint does not match "
                + "the committed transcript.");
        }

        ProtocolValidator.EnsureValid(run);
        if (terminalOutcome is not null
            && !string.Equals(
                run.State,
                RunStates.Failed,
                StringComparison.Ordinal)
            && !string.Equals(
                run.State,
                RunStates.Cancelled,
                StringComparison.Ordinal)
            && !string.Equals(
                run.State,
                RunStates.Reconciling,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Terminal-outcome metadata is not bound to a failed, "
                + "cancelled, or reconciling run.");
        }

        foreach (var toolCall in assistantToolCalls.Values)
        {
            if (RunStateMachine.IsTerminal(run.State)
                || durableRequestCallIds.Contains(toolCall.ToolCallId)
                || completedToolCallIds.Contains(toolCall.ToolCallId))
            {
                continue;
            }

            var message = new NormalizedMessage
            {
                MessageId = "tool-dispatch-aborted:" + toolCall.ToolCallId,
                Role = NormalizedRoles.Tool,
                CreatedAt = toolCall.Timestamp,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromToolResult(
                        toolCall.ToolCallId,
                        toolCall.ToolName,
                        RuntimePromptBuilder.ErrorPayload(
                            IsRuntimeLocalControl(
                                toolCall.ToolName)
                            || SkillRuntimeControlNames.IsReserved(
                                toolCall.ToolName)
                                ? "tool_control_not_committed"
                                : "action_dispatch_not_committed",
                            "recovery",
                            IsRuntimeLocalControl(
                                toolCall.ToolName)
                            || SkillRuntimeControlNames.IsReserved(
                                toolCall.ToolName)
                                ? "The runtime control was not committed. "
                                  + "Replan the call."
                                : "The action batch was not committed and no "
                                  + "game action was dispatched. Replan the "
                                  + "call."))
                }
            };
            await _journal.AppendTranscriptAsync(
                    run,
                    message,
                    toolCall.TurnId,
                    toolCall.AttemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            transcript.Add(message);
        }

        foreach (var pair in terminalReceipts)
        {
            if (terminalReceiptEvents.Contains(pair.Key))
            {
                continue;
            }

            var recoveredReceipt = pair.Value;
            await _journal.AppendBuiltInDurableAsync(
                    run,
                    string.Equals(
                        recoveredReceipt.Receipt.Status,
                        ReceiptStatuses.Failed,
                        StringComparison.Ordinal)
                        ? RuntimeEventKinds.ToolFailed
                        : RuntimeEventKinds.ToolCompleted,
                    ProtocolJson.ToElement(recoveredReceipt.Receipt),
                    recoveredReceipt.TurnId,
                    recoveredReceipt.AttemptId,
                    eventId:
                        "tool-result-event:"
                        + recoveredReceipt.Receipt.OperationId
                        + ":"
                        + recoveredReceipt.Receipt.Revision.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var finalOutputCommittedEvidence = terminalReceipts.Values
            .Select(
                item => new FinalOutputCommittedEvidence(
                    run.RunId,
                    item.TurnId,
                    terminalReceiptSourceEventIds[
                        ReceiptKey(item.Receipt)],
                    item.Receipt))
            .OrderBy(
                item => item.Receipt.OperationId,
                StringComparer.Ordinal)
            .ThenBy(item => item.Receipt.Revision)
            .ToArray();
        var finalOutputAdmission =
            FinalOutputAdmissionBinding.Read(run);
        if (finalOutputAdmission is null)
        {
            if (finalOutputAdmissionEvidence.HasValue
                || finalOutputPresentation.HasValue
                || providerPresentationsByTurn.Count > 0)
            {
                throw new InvalidDataException(
                    "A non-strict run contains final-output admission "
                    + "metadata.");
            }
        }
        else if (finalOutput.HasValue)
        {
            if (!finalOutputAdmissionEvidence.HasValue
                || !finalOutputPresentation.HasValue
                || finalOutputTurnId is null
                || !assistantMessagesByTurn.TryGetValue(
                    finalOutputTurnId,
                    out var admittedAssistant))
            {
                throw new InvalidDataException(
                    "Final-output admission evidence has no admitted output.");
            }

            FinalOutputAdmissionCodec.ValidateEvidence(
                finalOutputAdmissionEvidence.Value,
                run,
                finalOutputTurnId,
                admittedAssistant,
                finalOutput.Value,
                finalOutputCommittedEvidence);
            _ = FinalOutputAdmissionCodec.ValidatePresentation(
                finalOutputPresentation.Value,
                finalOutputAdmissionEvidence.Value);
        }

        foreach (var presentation in providerPresentationsByTurn)
        {
            var admitted =
                finalOutputAdmissionEvidence.HasValue
                && string.Equals(
                    presentation.Key,
                    finalOutputTurnId,
                    StringComparison.Ordinal);
            var state = FinalOutputAdmissionCodec.ValidatePresentation(
                presentation.Value,
                admitted
                    ? finalOutputAdmissionEvidence
                    : null);
            if (admitted
                != string.Equals(
                    state,
                    FinalOutputAdmissionCodec
                        .AdmittedPresentationState,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A provider result has an invalid final-output "
                    + "presentation state.");
            }
        }

        var pendingMemoryCommits = preparedMemoryCommits.Values
            .Where(
                item => !completedMemoryCommitIds.Contains(
                    item.CommitId))
            .OrderBy(item => item.TurnId, StringComparer.Ordinal)
            .ToArray();
        return new RecoveredRun
        {
            Run = run,
            Transcript = transcript,
            LastTurnSnapshot = lastSnapshot,
            FinalOutput = finalOutput,
            ProviderOpaqueContinuationState =
                providerOpaqueContinuationState?.Snapshot(),
            FinalOutputTurnId = finalOutputTurnId,
            PendingOperations = pending,
            ReplaySafeTurnId = replaySafeTurnId,
            RecoveryContext = recoveryContext,
            RecoveryActiveSkills = recoveryActiveSkills,
            RecoverySkillActivationState = recoverySkillState,
            RecoveryWorkloadClass = ResolveRecoveryWorkloadClass(
                lastSnapshot,
                initialInput),
            RecoveryExecutionMode = initialInput?.ExecutionMode
                                    ?? DurableExecutionModes.Agent,
            RecoveryInference = initialInput?.Inference?.CloneValidated(),
            RecoveryRoutePreference =
                initialInput?.RoutePreference?.CloneValidated(),
            RecoveryToolActivations = lastToolDisclosure?.Activations
                .Select(item => item.Clone())
                .ToArray()
                ?? Array.Empty<ToolActivationRecord>(),
            PendingMemoryCommits = pendingMemoryCommits,
            CompletedMemoryCommitIds = completedMemoryCommitIds
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            MemoryReceiptBatches = terminalReceipts.Values
                .GroupBy(item => item.TurnId, StringComparer.Ordinal)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(
                    group => new RecoveredMemoryReceiptBatch(
                        group.Key,
                        group.Select(item => item.Receipt).ToArray(),
                        group.Select(
                                item => terminalReceiptSourceEventIds[
                                    ReceiptKey(item.Receipt)])
                            .ToArray()))
                .ToArray(),
            RecoveryActionRequests = requests.Values
                .OrderBy(item => item.OperationId, StringComparer.Ordinal)
                .Select(
                    item => ProtocolJson.DeserializeActionRequest(
                        ProtocolJson.Serialize(item)))
                .ToArray(),
            GameContextAdvancedTurnIds = gameContextAdvancedTurnIds
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            CommittedProviderResultTurnIds =
                committedProviderResultTurnIds
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray(),
            FinalOutputAdmissionAttempts =
                finalOutputAdmissionAttempts,
            MemoryCommitRecords =
                new ReadOnlyDictionary<string, PreparedRuntimeMemoryCommit>(
                    new Dictionary<string, PreparedRuntimeMemoryCommit>(
                        preparedMemoryCommits,
                        StringComparer.Ordinal)),
            CommittedMemorySourceEventIdsByTurn =
                new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                    memorySourceEventIdsByTurn.ToDictionary(
                        item => item.Key,
                        item => (IReadOnlyList<string>)item.Value
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToArray(),
                        StringComparer.Ordinal)),
            CommittedAssistantMessagesByTurn =
                new ReadOnlyDictionary<string, NormalizedMessage>(
                    assistantMessagesByTurn
                        .Where(
                            item => committedProviderResultTurnIds.Contains(
                                item.Key))
                        .ToDictionary(
                            item => item.Key,
                            item => item.Value,
                            StringComparer.Ordinal)),
            UnsettledProviderDispatches =
                unsettledProviderDispatches.Values
                    .OrderBy(item => item.StreamAttemptId, StringComparer.Ordinal)
                    .ToArray(),
            FinalOutputCommittedEvidence =
                finalOutputCommittedEvidence,
            FinalOutputAdmissionEvidence =
                finalOutputAdmissionEvidence?.Clone(),
            TerminalOutcome = terminalOutcome
        };
    }

    private static IReadOnlyList<RuntimeEvent> ValidateRecoveryEvents(
        string runId,
        IReadOnlyList<RuntimeEvent> events,
        int maxEvents,
        int maxEventUtf8Bytes,
        int maxAggregateUtf8Bytes)
    {
        int eventCount;
        try
        {
            eventCount = events.Count;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                "The journal event collection did not expose a stable count.",
                exception);
        }

        if (eventCount < 0)
        {
            throw new InvalidDataException(
                "The journal event collection returned a negative count.");
        }

        if (eventCount > maxEvents)
        {
            throw new RunRecoveryCapacityExceededException(
                runId,
                maxEvents,
                eventCount);
        }

        var snapshots = new RuntimeEvent[eventCount];
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        var alreadyOrdered = true;
        long aggregateUtf8Bytes = 0;
        for (var index = 0; index < eventCount; index++)
        {
            RuntimeEvent? candidate;
            try
            {
                candidate = events[index];
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                throw new InvalidDataException(
                    "The journal event collection did not match its "
                    + "declared count.",
                    exception);
            }

            var runtimeEvent = candidate;
            if (runtimeEvent is null)
            {
                throw new InvalidDataException(
                    "The journal returned a null runtime event.");
            }

            try
            {
                ProtocolValidator.EnsureValid(runtimeEvent);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "The journal returned an invalid runtime event.",
                    exception);
            }

            var remainingAggregate = maxAggregateUtf8Bytes
                                     - aggregateUtf8Bytes;
            if (remainingAggregate <= 0)
            {
                throw new RunRecoveryBytesCapacityExceededException(
                    runId,
                    maxAggregateUtf8Bytes,
                    checked(aggregateUtf8Bytes + 1));
            }

            var effectiveEventLimit = (int)Math.Min(
                maxEventUtf8Bytes,
                remainingAggregate);
            RuntimeEvent snapshot;
            try
            {
                using var buffer = new RecoveryEventBufferWriter(
                    effectiveEventLimit);
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    JsonSerializer.Serialize(
                        writer,
                        runtimeEvent,
                        ProtocolJsonContext.Default.RuntimeEvent);
                    writer.Flush();
                }

                aggregateUtf8Bytes = checked(
                    aggregateUtf8Bytes
                    + buffer.WrittenCount);
                snapshot = JsonSerializer.Deserialize(
                               buffer.WrittenSpan,
                               ProtocolJsonContext.Default.RuntimeEvent)
                           ?? throw new InvalidDataException(
                               "The journal returned a null runtime event "
                               + "snapshot.");
            }
            catch (RecoveryEventBufferLimitException)
            {
                if (effectiveEventLimit < maxEventUtf8Bytes)
                {
                    throw new RunRecoveryBytesCapacityExceededException(
                        runId,
                        maxAggregateUtf8Bytes,
                        checked((long)maxAggregateUtf8Bytes + 1));
                }

                throw new RunRecoveryEventCapacityExceededException(
                    runId,
                    maxEventUtf8Bytes);
            }
            catch (Exception exception)
                when (exception is JsonException
                      or InvalidOperationException
                      or NotSupportedException
                      or OverflowException)
            {
                throw new InvalidDataException(
                    "The journal returned a runtime event that could not "
                    + "be snapshotted.",
                    exception);
            }

            runtimeEvent = snapshot;
            snapshots[index] = runtimeEvent;
            if (!string.Equals(
                    runtimeEvent.RunId,
                    runId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A recovered runtime event belongs to a different run.");
            }

            if (!string.Equals(
                    runtimeEvent.Durability,
                    EventDurabilities.Durable,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A recovered runtime event is not durable.");
            }

            if (!eventIds.Add(runtimeEvent.EventId))
            {
                throw new InvalidDataException(
                    "The journal contains duplicate runtime event ids.");
            }

            if (!sequences.Add(runtimeEvent.Sequence))
            {
                throw new InvalidDataException(
                    "The journal contains duplicate runtime event sequences.");
            }

            if (runtimeEvent.Sequence != index)
            {
                alreadyOrdered = false;
            }
        }

        if (alreadyOrdered)
        {
            return snapshots;
        }

        Array.Sort(
            snapshots,
            static (left, right) =>
                left.Sequence.CompareTo(right.Sequence));
        for (var index = 0; index < snapshots.Length; index++)
        {
            if (snapshots[index].Sequence != index)
            {
                throw new InvalidDataException(
                    "The journal contains a missing or non-contiguous "
                    + "runtime event sequence.");
            }
        }

        return snapshots;
    }

    private sealed class RecoveryEventBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private const int WriterSlackBytes = 4_096;

        private readonly int _maximumBytes;
        private byte[]? _buffer;
        private int _written;

        public RecoveryEventBufferWriter(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _buffer = ArrayPool<byte>.Shared.Rent(
                checked(maximumBytes + WriterSlackBytes));
        }

        public int WrittenCount => _written;

        public ReadOnlySpan<byte> WrittenSpan =>
            Buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || count > _maximumBytes - _written)
            {
                throw new RecoveryEventBufferLimitException();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var available = checked(
                _maximumBytes - _written + WriterSlackBytes);
            if (sizeHint < 0 || sizeHint > available)
            {
                throw new RecoveryEventBufferLimitException();
            }

            return Buffer.AsMemory(_written, available);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            var available = checked(
                _maximumBytes - _written + WriterSlackBytes);
            if (sizeHint < 0 || sizeHint > available)
            {
                throw new RecoveryEventBufferLimitException();
            }

            return Buffer.AsSpan(_written, available);
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private byte[] Buffer =>
            _buffer ?? throw new ObjectDisposedException(
                nameof(RecoveryEventBufferWriter));
    }

    private sealed class RecoveryEventBufferLimitException : Exception
    {
    }

    internal static string ResolveRecoveryWorkloadClass(
        TurnSnapshot? lastSnapshot,
        DurableRunInputSnapshot? initialInput)
    {
        if (lastSnapshot?.Extensions.TryGetValue(
                "providerWorkloadClass",
                out var value) == true)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "A turn snapshot contains an invalid provider workload class.");
            }

            try
            {
                return ProviderWorkloadClasses.Normalize(
                    value.GetString(),
                    "providerWorkloadClass");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "A turn snapshot contains an invalid provider workload class.",
                    exception);
            }
        }

        return initialInput?.WorkloadClass
               ?? ProviderWorkloadClasses.Interactive;
    }

    public async ValueTask<RecoveredRun> ReconcileAsync(
        RecoveredRun recovered,
        IGameOperationReconciler reconciler,
        string attemptId,
        CancellationToken cancellationToken)
    {
        if (recovered is null)
        {
            throw new ArgumentNullException(nameof(recovered));
        }

        if (reconciler is null)
        {
            throw new ArgumentNullException(nameof(reconciler));
        }

        var pendingOperations = ValidatePendingOperations(
            recovered.Run,
            recovered.PendingOperations,
            cursor: new RunJournalCursor(
                recovered.Run.RunId,
                recovered.Run.Revision,
                recovered.Run.Revision));
        foreach (var pending in pendingOperations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProtocolValidator.EnsureValid(pending.Request);
            JsonValueInspector.ValidateAndMeasure(
                ProtocolJson.ToElement(pending.Request),
                new JsonValueLimits(),
                nameof(pending.Request));
            var hostReceipt = await QueryOperationWithDeadlineAsync(
                    reconciler,
                    pending.Request,
                    cancellationToken)
                .ConfigureAwait(false);
            if (hostReceipt is null)
            {
                break;
            }

            var receipt = ValidateReconciledReceipt(
                pending.Request,
                hostReceipt,
                recovered.Run);
            if (!string.Equals(
                    receipt.OperationId,
                    pending.OperationId,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    pending.OperationId,
                    "the host returned a receipt for a different operation.");
            }

            var receiptSourceEventId =
                await _journal.AppendActionReceiptAsync(
                    recovered.Run,
                    pending.Request.TurnId,
                    attemptId,
                    receipt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!string.Equals(
                    receipt.Status,
                    ReceiptStatuses.Unknown,
                    StringComparison.Ordinal))
            {
                recovered.FinalOutputCommittedEvidence =
                    recovered.FinalOutputCommittedEvidence
                        .Concat(
                            new[]
                            {
                                new FinalOutputCommittedEvidence(
                                    recovered.Run.RunId,
                                    pending.Request.TurnId,
                                    receiptSourceEventId,
                                    receipt)
                            })
                        .OrderBy(
                            item => item.Receipt.OperationId,
                            StringComparer.Ordinal)
                        .ThenBy(item => item.Receipt.Revision)
                        .ToArray();
                recovered.MemoryReceiptBatches =
                    AddRecoveredMemoryReceipt(
                        recovered.MemoryReceiptBatches,
                        pending.Request.TurnId,
                        receipt,
                        receiptSourceEventId);
                recovered.CommittedMemorySourceEventIdsByTurn =
                    AddRecoveredMemorySource(
                        recovered.CommittedMemorySourceEventIdsByTurn,
                        pending.Request.TurnId,
                        receiptSourceEventId);
                var message = NormalizedTranscript.ToolResult(
                    ToolResultMessageId(receipt),
                    pending.Request.ToolCallId,
                    pending.Request.ActionName,
                    FinalOutputAdmissionBinding.Read(
                            recovered.Run) is null
                        ? ProtocolJson.ToElement(receipt)
                        : FinalOutputAdmissionCodec
                            .ForModelPresentation(
                                receipt,
                                receiptSourceEventId),
                    receipt.ReceivedAt);
                await _journal.AppendTranscriptAsync(
                        recovered.Run,
                        message,
                        pending.Request.TurnId,
                        attemptId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!recovered.Transcript.Any(
                        item => string.Equals(
                            item.MessageId,
                            message.MessageId,
                            StringComparison.Ordinal)))
                {
                    recovered.Transcript = recovered.Transcript
                        .Concat(new[] { message })
                        .ToArray();
                }
            }
        }

        var remainingCandidate =
            await _operations.ReadPendingOperationsAsync(
                recovered.Run.RunId,
                CancellationToken.None)
            .ConfigureAwait(false);
        var remaining = ValidatePendingOperations(
            recovered.Run,
            remainingCandidate,
            cursor: new RunJournalCursor(
                recovered.Run.RunId,
                recovered.Run.Revision,
                recovered.Run.Revision));
        recovered.PendingOperations = remaining;
        recovered.Run.PendingOperationIds = remaining
            .Select(item => item.OperationId)
            .ToList();
        return recovered;
    }

    private static IReadOnlyList<RecoveredMemoryReceiptBatch>
        AddRecoveredMemoryReceipt(
            IReadOnlyList<RecoveredMemoryReceiptBatch> batches,
            string turnId,
            ActionReceipt receipt,
            string sourceEventId)
    {
        var count = batches.Count;
        var result = new List<RecoveredMemoryReceiptBatch>(count + 1);
        var replaced = false;
        for (var index = 0; index < count; index++)
        {
            var batch = batches[index]
                        ?? throw new InvalidDataException(
                            "A recovered memory receipt batch is null.");
            if (!string.Equals(
                    batch.TurnId,
                    turnId,
                    StringComparison.Ordinal))
            {
                result.Add(batch);
                continue;
            }

            var receipts = batch.Receipts
                .Where(
                    item => !string.Equals(
                        item.OperationId,
                        receipt.OperationId,
                        StringComparison.Ordinal))
                .Append(receipt)
                .ToArray();
            result.Add(
                new RecoveredMemoryReceiptBatch(
                    turnId,
                    receipts,
                    batch.SourceEventIds.Append(sourceEventId).ToArray()));
            replaced = true;
        }

        if (!replaced)
        {
            result.Add(
                new RecoveredMemoryReceiptBatch(
                    turnId,
                    new[] { receipt },
                    new[] { sourceEventId }));
        }

        return result
            .OrderBy(item => item.TurnId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>
        AddRecoveredMemorySource(
            IReadOnlyDictionary<string, IReadOnlyList<string>> sources,
            string turnId,
            string eventId)
    {
        var result = sources.ToDictionary(
            item => item.Key,
            item => item.Value.ToList(),
            StringComparer.Ordinal);
        if (!result.TryGetValue(turnId, out var turnSources))
        {
            turnSources = new List<string>();
            result.Add(turnId, turnSources);
        }

        if (!turnSources.Contains(eventId, StringComparer.Ordinal))
        {
            turnSources.Add(eventId);
            turnSources.Sort(StringComparer.Ordinal);
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            result.ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<string>)item.Value.ToArray(),
                StringComparer.Ordinal));
    }

    private async ValueTask<ActionReceipt?>
        QueryOperationWithDeadlineAsync(
            IGameOperationReconciler reconciler,
            ActionRequest request,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_reconciliationQueries.TryAcquire(
                request,
                out var reconciliationLease))
        {
            return null;
        }

        Task<ActionReceipt>? query = null;
        try
        {
            var queryRequest = ProtocolJson.DeserializeActionRequest(
                ProtocolJson.Serialize(request));
            cancellationToken.ThrowIfCancellationRequested();
            query = reconciler.QueryOperationAsync(
                    queryRequest,
                    cancellationToken)
                .AsTask();
            if (!cancellationToken.CanBeCanceled)
            {
                try
                {
                    return await query.ConfigureAwait(false);
                }
                finally
                {
                    query = null;
                }
            }

            var cancellationSignal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(
                () => cancellationSignal.TrySetResult(true));
            var completed = await Task.WhenAny(
                    query,
                    cancellationSignal.Task)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, query))
            {
                var detachedQuery = query!;
                query = null;
                var detachedLease = reconciliationLease!;
                reconciliationLease = null;
                _ = ObserveDetachedQueryAsync(
                    detachedQuery,
                    detachedLease);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }

            try
            {
                return await query.ConfigureAwait(false);
            }
            finally
            {
                query = null;
            }
        }
        catch
        {
            if (query is not null)
            {
                var detachedQuery = query;
                query = null;
                var detachedLease = reconciliationLease!;
                reconciliationLease = null;
                _ = ObserveDetachedQueryAsync(
                    detachedQuery,
                    detachedLease);
            }

            throw;
        }
        finally
        {
            reconciliationLease?.Dispose();
        }
    }

    private static async Task ObserveDetachedQueryAsync(
        Task<ActionReceipt> query,
        ReconciliationQueryRegistry.ReconciliationQueryLease lease)
    {
        try
        {
            _ = await query.ConfigureAwait(false);
        }
        catch
        {
            // The run has already crossed its deadline. Observing a late
            // reconciliation failure prevents an unobserved task exception.
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static ActionReceipt ValidateReconciledReceipt(
        ActionRequest request,
        ActionReceipt hostReceipt,
        AgentRun run)
    {
        if (hostReceipt is null)
        {
            throw new InvalidDataException(
                "The operation reconciler returned a null receipt.");
        }

        var receipt = ActionReceiptIngressValidator.ValidateAndClone(
            request,
            hostReceipt,
            run);
        var limits = new JsonValueLimits();

        if (string.Equals(
                receipt.Status,
                ReceiptStatuses.Succeeded,
                StringComparison.Ordinal)
            && request.Extensions.TryGetValue(
                ResultSchemaExtension,
                out var resultSchema))
        {
            JsonValueInspector.ValidateAndMeasure(
                resultSchema,
                limits,
                ResultSchemaExtension);
            var result = receipt.Result
                         ?? ProtocolJson.ParseElement("null");
            var validation = new ToolArgumentValidator().Validate(
                resultSchema,
                result);
            if (!validation.IsValid)
            {
                receipt.Result = null;
                receipt.ErrorCode = "tool_result_schema_invalid";
                receipt.Retryable = false;
            }
        }

        return receipt;
    }

    private static void AddMemorySource(
        IDictionary<string, HashSet<string>> sourceIdsByTurn,
        string turnId,
        string eventId)
    {
        if (!sourceIdsByTurn.TryGetValue(turnId, out var sourceIds))
        {
            sourceIds = new HashSet<string>(StringComparer.Ordinal);
            sourceIdsByTurn.Add(turnId, sourceIds);
        }

        if (!sourceIds.Add(eventId))
        {
            throw new InvalidDataException(
                "A committed memory source event identity is duplicated.");
        }
    }

    private static void EnsureDerivedEventId(
        RuntimeEvent runtimeEvent,
        string runId,
        string candidate)
    {
        if (!string.Equals(
                runtimeEvent.EventId,
                RuntimeEventIdDerivation.Derive(runId, candidate),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A durable runtime event has an inconsistent semantic "
                + "identity.");
        }
    }

    private static AgentRun RequireMemoryOutboxPosition(
        RuntimeEvent runtimeEvent,
        AgentRun? run,
        TurnSnapshot? lastSnapshot,
        ISet<string> startedTurnIds,
        bool allowAfterTerminal)
    {
        if (run is null
            || string.IsNullOrWhiteSpace(runtimeEvent.TurnId)
            || runtimeEvent.RuntimeGeneration != run.RuntimeGeneration
            || (!allowAfterTerminal || !RunStateMachine.IsTerminal(run.State))
            && !string.Equals(
                run.CurrentTurnId,
                runtimeEvent.TurnId,
                StringComparison.Ordinal)
            || lastSnapshot is null
            || !string.Equals(
                lastSnapshot.TurnId,
                runtimeEvent.TurnId,
                StringComparison.Ordinal)
            || lastSnapshot.RuntimeGeneration
            != runtimeEvent.RuntimeGeneration
            || !startedTurnIds.Contains(runtimeEvent.TurnId)
            || !allowAfterTerminal && RunStateMachine.IsTerminal(run.State))
        {
            throw new InvalidDataException(
                "A memory outbox event is outside its committed turn.");
        }

        return run;
    }

    private static IReadOnlyList<string> RequireMemorySources(
        IReadOnlyDictionary<string, HashSet<string>> sourceIdsByTurn,
        IEnumerable<RecoveredReceipt> terminalReceipts,
        IReadOnlyDictionary<string, string> receiptSourceEventIds,
        string turnId)
    {
        var receiptSources = terminalReceipts
            .Where(
                item => string.Equals(
                    item.TurnId,
                    turnId,
                    StringComparison.Ordinal))
            .Select(
                item => receiptSourceEventIds.TryGetValue(
                    ReceiptKey(item.Receipt),
                    out var eventId)
                    ? eventId
                    : throw new InvalidDataException(
                        "A terminal receipt has no committed source event."))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (receiptSources.Length > 0)
        {
            return receiptSources;
        }

        if (!sourceIdsByTurn.TryGetValue(turnId, out var sourceIds)
            || sourceIds.Count == 0)
        {
            throw new InvalidDataException(
                "A memory outbox event has no committed source evidence.");
        }

        return sourceIds.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateRecoveredMemoryCommit(
        PreparedRuntimeMemoryCommit prepared,
        AgentRun run,
        IReadOnlyCollection<string> sourceEventIds,
        TurnSnapshot snapshot)
    {
        if (prepared.Mutations.Count > 256
            || !string.Equals(
                prepared.PayloadDigest,
                RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(
                    prepared.Mutations),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A recovered memory commit has invalid capacity or digest.");
        }

        if (!snapshot.Extensions.TryGetValue(
                RuntimeMemoryAgentLoop.PolicySnapshotExtension,
                out var policy)
            || policy.ValueKind != JsonValueKind.Object
            || !policy.TryGetProperty("policyId", out var policyId)
            || !policy.TryGetProperty("version", out var policyVersion)
            || policyId.ValueKind != JsonValueKind.String
            || policyVersion.ValueKind != JsonValueKind.String
            || !string.Equals(
                policyId.GetString(),
                prepared.PolicyId,
                StringComparison.Ordinal)
            || !string.Equals(
                policyVersion.GetString(),
                prepared.PolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A recovered memory commit does not match its turn policy.");
        }

        foreach (var mutation in prepared.Mutations)
        {
            if (mutation.Kind != MemoryMutationKind.Upsert)
            {
                continue;
            }

            var provenance = mutation.Record?.Provenance;
            if (provenance is null
                || !provenance.Committed
                || !string.Equals(
                    provenance.WorldId,
                    run.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    provenance.SourceRunId,
                    run.RunId,
                    StringComparison.Ordinal)
                || provenance.SessionId is not null
                && !string.Equals(
                    provenance.SessionId,
                    run.SessionId,
                    StringComparison.Ordinal)
                || !sourceEventIds.Contains(
                    provenance.SourceEventId,
                    StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "A recovered memory mutation cites uncommitted evidence.");
            }
        }
    }

    private static AgentRun RequireRunForReceipt(AgentRun? run)
    {
        return run ?? throw new InvalidDataException(
            "A recovered receipt precedes the first valid run checkpoint.");
    }

    private static void EnsureReceiptEventMatchesRequest(
        RuntimeEvent runtimeEvent,
        ActionRequest request)
    {
        if (!string.Equals(
                runtimeEvent.RunId,
                request.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                runtimeEvent.TurnId,
                request.TurnId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A recovered receipt event does not match its action request.");
        }
    }

    private static void ValidateCursor(
        string runId,
        int eventCount,
        RunJournalCursor? cursor)
    {
        if (cursor is null
            || !string.Equals(
                cursor.RunId,
                runId,
                StringComparison.Ordinal)
            || cursor.NextSequence != eventCount
            || cursor.Revision != eventCount)
        {
            throw new InvalidDataException(
                "The durable run cursor does not match the recovered journal.");
        }
    }

    private static IReadOnlyList<OperationLedgerEntry>
        ValidatePendingOperations(
            AgentRun run,
            IReadOnlyList<OperationLedgerEntry>? operations,
            IReadOnlyDictionary<string, ActionRequest>? journalRequests = null,
            IReadOnlyDictionary<string, long>? requestEventSequences = null,
            IReadOnlyDictionary<string, ActionReceipt>?
                latestJournalReceipts = null,
            IReadOnlyDictionary<string, long>?
                latestJournalReceiptSequences = null,
            RunJournalCursor? cursor = null)
    {
        if (run is null)
        {
            throw new InvalidDataException(
                "Pending operations require a recovered run.");
        }

        ProtocolValidator.EnsureValid(run);
        if (operations is null)
        {
            throw new InvalidDataException(
                "The operation ledger returned a null pending collection.");
        }

        int operationCount;
        try
        {
            operationCount = operations.Count;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                "The operation ledger pending collection did not expose "
                + "a stable count.",
                exception);
        }

        if (operationCount < 0)
        {
            throw new InvalidDataException(
                "The operation ledger returned a negative pending count.");
        }

        if (operationCount > run.Budget.MaxActions)
        {
            throw new InvalidDataException(
                "The operation ledger returned more pending operations "
                + "than the run action budget permits.");
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new List<OperationLedgerEntry>(operationCount);
        for (var index = 0; index < operationCount; index++)
        {
            OperationLedgerEntry? candidate;
            try
            {
                candidate = operations[index];
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                throw new InvalidDataException(
                    "The operation ledger pending collection did not match "
                    + "its declared count.",
                    exception);
            }

            if (candidate is null || candidate.Request is null)
            {
                throw new InvalidDataException(
                    "The operation ledger returned an invalid pending entry.");
            }

            var request = ProtocolJson.DeserializeActionRequest(
                ProtocolJson.Serialize(candidate.Request));
            ProtocolValidator.EnsureValid(request);
            if (!operationIds.Add(request.OperationId))
            {
                throw new InvalidDataException(
                    "The operation ledger returned duplicate operation ids.");
            }

            if (!candidate.IsPending
                || !string.Equals(
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
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A pending operation does not belong to the recovered "
                    + "run identity.");
            }

            if (candidate.RequestSequence < 0
                || candidate.RequestSequence == long.MaxValue
                || candidate.RequestRunRevision
                != candidate.RequestSequence + 1
                || cursor is not null
                && (candidate.RequestSequence >= cursor.NextSequence
                    || candidate.RequestRunRevision > cursor.Revision))
            {
                throw new InvalidDataException(
                    "A pending operation has an invalid request cursor.");
            }

            if (journalRequests is not null)
            {
                if (!journalRequests.TryGetValue(
                        request.OperationId,
                        out var journalRequest)
                    || !string.Equals(
                        ProtocolJson.Serialize(request),
                        ProtocolJson.Serialize(journalRequest),
                        StringComparison.Ordinal)
                    || requestEventSequences is null
                    || !requestEventSequences.TryGetValue(
                        request.OperationId,
                        out var requestSequence)
                    || requestSequence != candidate.RequestSequence)
                {
                    throw new InvalidDataException(
                        "A pending operation does not match its journaled "
                        + "action request.");
                }
            }

            ActionReceipt? receiptSnapshot = null;
            if (candidate.LatestReceipt is null)
            {
                if (candidate.LatestReceiptSequence.HasValue
                    || candidate.LatestReceiptRunRevision.HasValue
                    || latestJournalReceipts?.ContainsKey(
                        request.OperationId) == true)
                {
                    throw new InvalidDataException(
                        "A pending operation has inconsistent receipt "
                        + "metadata.");
                }
            }
            else
            {
                receiptSnapshot =
                    ActionReceiptIngressValidator.ValidateAndClone(
                        request,
                        candidate.LatestReceipt,
                        run);
                if (!string.Equals(
                        receiptSnapshot.Status,
                        ReceiptStatuses.Unknown,
                        StringComparison.Ordinal)
                    || !candidate.LatestReceiptSequence.HasValue
                    || !candidate.LatestReceiptRunRevision.HasValue
                    || candidate.LatestReceiptSequence.Value
                    <= candidate.RequestSequence
                    || candidate.LatestReceiptSequence.Value
                    == long.MaxValue
                    || candidate.LatestReceiptRunRevision.Value
                    != candidate.LatestReceiptSequence.Value + 1
                    || cursor is not null
                    && (candidate.LatestReceiptSequence.Value
                            >= cursor.NextSequence
                        || candidate.LatestReceiptRunRevision.Value
                            > cursor.Revision))
                {
                    throw new InvalidDataException(
                        "A pending operation has an invalid latest receipt.");
                }

                if (latestJournalReceipts is not null
                    && (!latestJournalReceipts.TryGetValue(
                            request.OperationId,
                            out var journalReceipt)
                        || !string.Equals(
                            ProtocolJson.Serialize(receiptSnapshot),
                            ProtocolJson.Serialize(journalReceipt),
                            StringComparison.Ordinal)
                        || latestJournalReceiptSequences is null
                        || !latestJournalReceiptSequences.TryGetValue(
                            request.OperationId,
                            out var receiptSequence)
                        || receiptSequence
                        != candidate.LatestReceiptSequence.Value))
                {
                    throw new InvalidDataException(
                        "A pending operation does not match its journaled "
                        + "latest receipt.");
                }
            }

            snapshots.Add(
                new OperationLedgerEntry(
                    request,
                    receiptSnapshot,
                    candidate.RequestSequence,
                    candidate.RequestRunRevision,
                    candidate.LatestReceiptSequence,
                    candidate.LatestReceiptRunRevision));
        }

        return snapshots;
    }

    private static bool MessagesAreEquivalent(
        NormalizedMessage left,
        NormalizedMessage right)
    {
        return string.Equals(
            NormalizedMessageJournalCodec.Encode(left).GetRawText(),
            NormalizedMessageJournalCodec.Encode(right).GetRawText(),
            StringComparison.Ordinal);
    }

    private static bool ReceiptsAreEquivalent(
        ActionReceipt left,
        ActionReceipt right)
    {
        var canonical = ProtocolJson.DeserializeActionReceipt(
            ProtocolJson.Serialize(left));
        canonical.ReceivedAt = right.ReceivedAt;
        return string.Equals(
            ProtocolJson.Serialize(canonical),
            ProtocolJson.Serialize(right),
            StringComparison.Ordinal);
    }

    internal static string ToolResultMessageId(ActionReceipt receipt)
    {
        return "tool-result:"
               + receipt.OperationId
               + ":"
               + receipt.Revision.ToString(
                   System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReceiptKey(ActionReceipt receipt)
    {
        return receipt.OperationId
               + "\0"
               + receipt.Revision.ToString(
                   System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsProviderDispatchSettlement(string kind)
    {
        return kind == RuntimeEventKinds.BudgetUpdated
            || kind == RuntimeEventKinds.ProviderDispatchKnownZero
            || kind == RuntimeEventKinds.ProviderResultCommitted
            || kind == RuntimeEventKinds.ProviderResultDiscarded
            || kind == RuntimeEventKinds.ProviderUsageUncertain;
    }

    private static bool IsRuntimeLocalControl(string name)
    {
        return ToolDisclosureControlNames.IsReserved(name)
               || SkillRuntimeControlNames.IsReserved(name)
               || string.Equals(
                   name,
                   FinalOutputAdmissionControl.SubmitToolName,
                   StringComparison.Ordinal);
    }

    private static bool IsTurnOutputMessage(NormalizedMessage message)
    {
        return string.Equals(
                   message.Role,
                   NormalizedRoles.Assistant,
                   StringComparison.Ordinal)
               || string.Equals(
                   message.Role,
                   NormalizedRoles.Tool,
                   StringComparison.Ordinal);
    }

    private static IReadOnlyList<SkillActivationStateRecord>
        ReadActivatedSkillState(
            IReadOnlyList<NormalizedMessage> transcript,
            IReadOnlyDictionary<string, string?> transcriptTurnIds,
            string turnId)
    {
        var current = new List<SkillActivationStateRecord>();
        List<SkillActivationStateRecord>? matchingTurn = null;
        foreach (var message in transcript)
        {
            foreach (var part in message.Parts)
            {
                if (!part.Json.HasValue
                    || part.Json.Value.ValueKind != JsonValueKind.Object
                    || !part.Json.Value.TryGetProperty(
                        "contentType",
                        out var contentType)
                    || contentType.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = part.Json.Value;
                if (string.Equals(
                        message.Role,
                        NormalizedRoles.System,
                        StringComparison.Ordinal)
                    && string.Equals(
                        contentType.GetString(),
                        "application/vnd.game-agent.skills+json",
                        StringComparison.Ordinal))
                {
                    current = ReadDisclosureState(value);
                }
                else if (string.Equals(
                             message.Role,
                             NormalizedRoles.Tool,
                             StringComparison.Ordinal)
                         && string.Equals(
                             contentType.GetString(),
                             "application/vnd.game-agent."
                             + "skill-activation-result+json",
                             StringComparison.Ordinal)
                         && value.TryGetProperty(
                             "activated",
                             out var activated)
                         && activated.ValueKind
                         is JsonValueKind.True or JsonValueKind.False
                         && activated.GetBoolean())
                {
                    var record = ReadActivation(value);
                    var existing = current.FirstOrDefault(
                        item => string.Equals(
                            item.Reference,
                            record.Reference,
                            StringComparison.Ordinal));
                    if (existing is not null
                        && !string.Equals(
                            existing.ContentDigest,
                            record.ContentDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "A durable skill activation changes an exact "
                            + "skill identity.");
                    }

                    if (existing is null)
                    {
                        current.Add(record);
                    }
                }

                if (transcriptTurnIds.TryGetValue(
                        message.MessageId,
                        out var messageTurnId)
                    && string.Equals(
                        messageTurnId,
                        turnId,
                        StringComparison.Ordinal))
                {
                    matchingTurn = current
                        .Select(value => value.Clone())
                        .ToList();
                }
            }
        }

        return (matchingTurn ?? current)
            .Select(value => value.Clone())
            .ToArray();

        static List<SkillActivationStateRecord> ReadDisclosureState(
            JsonElement value)
        {
            if (!value.TryGetProperty(
                    "activated",
                    out var activatedElement)
                || activatedElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "A durable skill disclosure has no activated-skill "
                    + "array.");
            }

            var next = new List<SkillActivationStateRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in activatedElement.EnumerateArray())
            {
                var record = ReadActivation(item);
                if (!seen.Add(record.Reference))
                {
                    throw new InvalidDataException(
                        "A durable skill disclosure contains duplicate "
                        + "activated skills.");
                }

                next.Add(record);
            }

            return next;
        }

        static SkillActivationStateRecord ReadActivation(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("skillId", out var skillId)
                || skillId.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String
                || (!value.TryGetProperty(
                         "skillDigest",
                         out var skillDigest)
                    && !value.TryGetProperty(
                        "digest",
                        out skillDigest))
                || skillDigest.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "A durable activated-skill entry is malformed.");
            }

            return new SkillActivationStateRecord(
                skillId.GetString()!,
                version.GetString()!,
                skillDigest.GetString()!);
        }
    }

    private static bool SkillStatesEquivalent(
        IReadOnlyList<SkillActivationStateRecord> left,
        IReadOnlyList<SkillActivationStateRecord> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var rightByReference = right.ToDictionary(
            value => value.Reference,
            StringComparer.Ordinal);
        return left.All(
            value => rightByReference.TryGetValue(
                         value.Reference,
                         out var candidate)
                     && string.Equals(
                         value.ContentDigest,
                         candidate.ContentDigest,
                         StringComparison.Ordinal));
    }

    private static bool SkillReferencesEquivalent(
        IReadOnlyList<SkillReference> left,
        IReadOnlyList<SkillReference> right)
    {
        return left.Count == right.Count
               && left.Select(value => value.Value)
                   .OrderBy(value => value, StringComparer.Ordinal)
                   .SequenceEqual(
                       right.Select(value => value.Value)
                           .OrderBy(value => value, StringComparer.Ordinal),
                       StringComparer.Ordinal);
    }

    private static ProviderCacheKey? ValidateRecoveredTurnSnapshotExtensions(
        TurnSnapshot snapshot,
        ProviderCacheKey? previousCacheKey,
        IReadOnlyList<SkillActivationStateRecord> expectedSkillState)
    {
        var hasCacheKey = snapshot.Extensions.TryGetValue(
            ProviderCacheTelemetry.KeyExtensionName,
            out var cacheKeyEvidence);
        var hasCacheDecision = snapshot.Extensions.TryGetValue(
            ProviderCacheTelemetry.DecisionExtensionName,
            out var cacheDecisionEvidence);
        if (hasCacheKey != hasCacheDecision)
        {
            throw new InvalidDataException(
                "A turn snapshot has partial provider-cache evidence.");
        }

        ProviderCacheKey? currentCacheKey = null;
        if (hasCacheKey)
        {
            try
            {
                currentCacheKey = ProviderCacheKey.FromJson(
                    cacheKeyEvidence);
                _ = ProviderCacheTelemetry.RestoreDecision(
                    cacheDecisionEvidence,
                    previousCacheKey,
                    currentCacheKey);
            }
            catch (Exception exception)
                when (exception is ArgumentException
                      or InvalidDataException)
            {
                throw new InvalidDataException(
                    "A turn snapshot has invalid provider-cache evidence.",
                    exception);
            }
        }

        if (snapshot.Extensions.TryGetValue(
                SkillActivationStateCodec.ExtensionName,
                out var activeSkillEvidence))
        {
            var activeSkillState = SkillActivationStateCodec.Decode(
                activeSkillEvidence,
                DurableRunInputJournalCodec.MaxActiveSkills);
            var stateDigests = activeSkillState
                .Select(value => value.ContentDigest)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var snapshotDigests = snapshot.SkillDigests
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!stateDigests.SequenceEqual(
                    snapshotDigests,
                    StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "A turn snapshot active-skill state does not match "
                    + "its skill digests.");
            }

            if (!SkillStatesEquivalent(
                    activeSkillState,
                    expectedSkillState))
            {
                throw new InvalidDataException(
                    "A turn snapshot active-skill state does not match "
                    + "the exact committed disclosure.");
            }
        }

        if (snapshot.Extensions.TryGetValue(
                ConversationContextView.CheckpointExtensionName,
                out var checkpointEvidence))
        {
            ConversationContextCheckpoint checkpoint;
            try
            {
                checkpoint = ConversationContextCheckpointCodec.Decode(
                    checkpointEvidence);
            }
            catch (Exception exception)
                when (exception is ArgumentException
                      or InvalidDataException
                      or RuntimeContentLimitException)
            {
                throw new InvalidDataException(
                    "A turn snapshot has an invalid conversation checkpoint.",
                    exception);
            }

            if (!string.Equals(
                    checkpoint.RunId,
                    snapshot.RunId,
                    StringComparison.Ordinal)
                || !snapshot.Extensions.TryGetValue(
                    "conversationContext",
                    out var conversationReport)
                || !string.Equals(
                    CanonicalJsonDigest.ComputeSha256(
                        checkpoint.Report.ToSnapshotExtension()),
                    CanonicalJsonDigest.ComputeSha256(conversationReport),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A conversation checkpoint does not match its turn "
                    + "snapshot.");
            }
        }

        return currentCacheKey;
    }

    private static void ValidateProviderCacheUsageEvidence(
        RuntimeEvent runtimeEvent)
    {
        if (!runtimeEvent.Extensions.TryGetValue(
                ProviderCacheTelemetry.UsageExtensionName,
                out var evidence))
        {
            return;
        }

        if (!string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.BudgetUpdated,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Provider cache usage evidence is attached to an invalid "
                + "event kind.");
        }

        try
        {
            _ = ProviderCacheUsageEvidence.FromJson(evidence);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                  or InvalidDataException)
        {
            throw new InvalidDataException(
                "A provider usage event has invalid cache evidence.",
                exception);
        }
    }

    private static void ValidateProviderResultRoute(
        RuntimeEvent runtimeEvent,
        RecoveredProviderDispatch dispatch)
    {
        var routeFields = new[]
        {
            runtimeEvent.ModelId,
            runtimeEvent.TransportDialect,
            runtimeEvent.ProviderCapabilityDigest,
            runtimeEvent.ProviderRouteDigest
        };
        var presentRouteFields = routeFields.Count(
            value => !string.IsNullOrWhiteSpace(value));
        if (presentRouteFields is not 0 and not 4)
        {
            throw new InvalidDataException(
                "A provider result has a partial route identity.");
        }

        var hasPolicyVersion = runtimeEvent.Extensions.TryGetValue(
            ProviderRouteJournalExtensions.PolicyVersion,
            out var policyVersion);
        var hasPolicyDigest = runtimeEvent.Extensions.TryGetValue(
            ProviderRouteJournalExtensions.PolicyDigest,
            out var policyDigest);
        var hasDialectSemanticDigest =
            runtimeEvent.Extensions.TryGetValue(
                ProviderWireRequestEvidence
                    .DialectSemanticDigestJournalExtensionName,
                out var dialectSemanticDigest);
        if (hasPolicyVersion != hasPolicyDigest
            || presentRouteFields == 0
                && (hasPolicyVersion || hasDialectSemanticDigest))
        {
            throw new InvalidDataException(
                "A provider result has partial route evidence.");
        }

        if (presentRouteFields == 0)
        {
            return;
        }

        if (!string.Equals(
                runtimeEvent.ModelId,
                dispatch.ModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                runtimeEvent.TransportDialect,
                dispatch.TransportDialect,
                StringComparison.Ordinal)
            || !string.Equals(
                runtimeEvent.ProviderCapabilityDigest,
                dispatch.ProviderCapabilityDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                runtimeEvent.ProviderRouteDigest,
                dispatch.ProviderRouteDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A provider result route does not match its dispatch.");
        }

        if (hasPolicyVersion
            && (policyVersion.ValueKind != JsonValueKind.String
                || policyDigest.ValueKind != JsonValueKind.String
                || !string.Equals(
                    policyVersion.GetString(),
                    dispatch.ProviderRoutePolicyVersion,
                    StringComparison.Ordinal)
                || !string.Equals(
                    policyDigest.GetString(),
                    dispatch.ProviderRoutePolicyDigest,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A provider result route policy does not match its dispatch.");
        }

        if (hasDialectSemanticDigest
            && (dialectSemanticDigest.ValueKind != JsonValueKind.String
                || !CanonicalJsonDigest.IsSha256(
                    dialectSemanticDigest.GetString())
                || dispatch.ProviderDialectSemanticDigest is not null
                    && !string.Equals(
                        dialectSemanticDigest.GetString(),
                        dispatch.ProviderDialectSemanticDigest,
                        StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A provider result has an invalid dialect semantic digest.");
        }
    }

    private static RecoveredProviderDispatch ApplyProviderDispatchSettlement(
        IDictionary<string, RecoveredProviderDispatch> dispatches,
        RuntimeEvent runtimeEvent)
    {
        if (!dispatches.TryGetValue(
                runtimeEvent.StreamAttemptId!,
                out var dispatch))
        {
            throw new InvalidDataException(
                "A provider settlement has no preceding dispatch.");
        }

        if (!string.Equals(
                runtimeEvent.ProviderId,
                dispatch.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                runtimeEvent.AttemptId,
                dispatch.ProviderAttemptId,
                StringComparison.Ordinal)
            || !string.Equals(
                runtimeEvent.TurnId,
                dispatch.TurnId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A provider settlement does not match its dispatch identity.");
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.BudgetUpdated,
                StringComparison.Ordinal))
        {
            if (dispatch.UsageSettled)
            {
                throw new InvalidDataException(
                    "A provider dispatch has more than one usage settlement.");
            }

            dispatch.UsageSettled = true;
            return dispatch;
        }

        if ((string.Equals(
                 runtimeEvent.Kind,
                 RuntimeEventKinds.ProviderResultCommitted,
                 StringComparison.Ordinal)
             || string.Equals(
                 runtimeEvent.Kind,
                 RuntimeEventKinds.ProviderResultDiscarded,
                 StringComparison.Ordinal))
            && !dispatch.UsageSettled)
        {
            throw new InvalidDataException(
                "A provider result settled before its usage.");
        }

        if (dispatch.UsageSettled
            && (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ProviderDispatchKnownZero,
                    StringComparison.Ordinal)
                || string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ProviderUsageUncertain,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A provider usage settlement is contradictory.");
        }

        dispatches.Remove(runtimeEvent.StreamAttemptId!);
        return dispatch;
    }

    private static RecoveredProviderDispatch ReadProviderDispatch(
        RuntimeEvent runtimeEvent)
    {
        if (string.IsNullOrWhiteSpace(runtimeEvent.ProviderId)
            || string.IsNullOrWhiteSpace(runtimeEvent.AttemptId)
            || string.IsNullOrWhiteSpace(runtimeEvent.StreamAttemptId)
            || string.IsNullOrWhiteSpace(runtimeEvent.TurnId))
        {
            throw new InvalidDataException(
                "A provider dispatch checkpoint is missing its identity.");
        }

        var routeFields = new[]
        {
            runtimeEvent.ModelId,
            runtimeEvent.TransportDialect,
            runtimeEvent.ProviderCapabilityDigest,
            runtimeEvent.ProviderRouteDigest
        };
        var presentRouteFields = routeFields.Count(
            value => !string.IsNullOrWhiteSpace(value));
        if (presentRouteFields is not 0 and not 4)
        {
            throw new InvalidDataException(
                "A provider dispatch checkpoint has a partial route identity.");
        }

        var hasPolicyVersion = runtimeEvent.Extensions.TryGetValue(
            ProviderRouteJournalExtensions.PolicyVersion,
            out var policyVersionElement);
        var hasPolicyDigest = runtimeEvent.Extensions.TryGetValue(
            ProviderRouteJournalExtensions.PolicyDigest,
            out var policyDigestElement);
        if (hasPolicyVersion != hasPolicyDigest
            || hasPolicyVersion && presentRouteFields != 4)
        {
            throw new InvalidDataException(
                "A provider dispatch checkpoint has a partial route-policy identity.");
        }

        string? policyVersion = null;
        string? policyDigest = null;
        if (hasPolicyVersion)
        {
            if (policyVersionElement.ValueKind != JsonValueKind.String
                || policyDigestElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "A provider dispatch checkpoint has an invalid route-policy identity.");
            }

            try
            {
                policyVersion = RuntimeGuard.RequiredUtf8(
                    policyVersionElement.GetString(),
                    128,
                    ProviderRouteJournalExtensions.PolicyVersion);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "A provider dispatch checkpoint has an invalid route-policy version.",
                    exception);
            }

            policyDigest = policyDigestElement.GetString();
            if (!CanonicalJsonDigest.IsSha256(policyDigest))
            {
                throw new InvalidDataException(
                    "A provider dispatch checkpoint has an invalid route-policy digest.");
            }
        }

        if (presentRouteFields == 4
            && !string.Equals(
                hasPolicyVersion
                    ? ProviderRouteIdentity.ComputeRouteDigest(
                        runtimeEvent.ProviderId,
                        runtimeEvent.ModelId!,
                        runtimeEvent.TransportDialect!,
                        runtimeEvent.ProviderCapabilityDigest!,
                        policyVersion!,
                        policyDigest!)
                    : ProviderRouteIdentity.ComputeRouteDigest(
                        runtimeEvent.ProviderId,
                        runtimeEvent.ModelId!,
                        runtimeEvent.TransportDialect!,
                        runtimeEvent.ProviderCapabilityDigest!),
                runtimeEvent.ProviderRouteDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A provider dispatch checkpoint has an invalid route digest.");
        }

        var hasWireEvidence = runtimeEvent.Extensions.TryGetValue(
            ProviderWireRequestEvidence.JournalExtensionName,
            out var wireEvidence);
        var hasDialectSemanticDigest =
            runtimeEvent.Extensions.TryGetValue(
                ProviderWireRequestEvidence
                    .DialectSemanticDigestJournalExtensionName,
                out var dialectSemanticDigestElement);
        var hasDialectContract = runtimeEvent.Extensions.TryGetValue(
            ProviderDialectContract.JournalExtensionName,
            out var dialectContractEvidence);
        var hasWireEvidenceDigest =
            runtimeEvent.Extensions.TryGetValue(
                ProviderWireRequestEvidence
                    .IntegrityDigestJournalExtensionName,
                out var wireEvidenceDigestElement);
        var wireEvidenceFieldCount =
            (hasWireEvidence ? 1 : 0)
            + (hasDialectSemanticDigest ? 1 : 0)
            + (hasDialectContract ? 1 : 0)
            + (hasWireEvidenceDigest ? 1 : 0);
        if (wireEvidenceFieldCount is not 0 and not 4
            || hasWireEvidence && presentRouteFields != 4)
        {
            throw new InvalidDataException(
                "A provider dispatch checkpoint has partial wire evidence.");
        }

        string? dialectSemanticDigest = null;
        ProviderDialectContract? dialectContract = null;
        if (hasWireEvidence)
        {
            try
            {
                if (dialectSemanticDigestElement.ValueKind
                        != JsonValueKind.String
                    || wireEvidenceDigestElement.ValueKind
                        != JsonValueKind.String)
                {
                    throw new InvalidDataException(
                        "Provider wire evidence digests must be strings.");
                }

                dialectSemanticDigest =
                    dialectSemanticDigestElement.GetString();
                var wireEvidenceDigest =
                    wireEvidenceDigestElement.GetString();
                dialectContract = ProviderDialectContract.Restore(
                    dialectContractEvidence);
                if (!CanonicalJsonDigest.IsSha256(
                        dialectSemanticDigest)
                    || !string.Equals(
                        dialectContract.Identifier,
                        runtimeEvent.TransportDialect,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        dialectContract.SemanticDigest,
                        dialectSemanticDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A provider dispatch has inconsistent dialect "
                        + "evidence.");
                }

                ProviderWireRequestEvidence.ValidateJournalEvidence(
                    wireEvidence,
                    runtimeEvent.ProviderId,
                    runtimeEvent.ProviderRouteDigest!,
                    runtimeEvent.TransportDialect!,
                    dialectSemanticDigest!,
                    dialectContract.RequestContentType,
                    wireEvidenceDigest!);
            }
            catch (Exception exception)
                when (exception is ProviderException
                      or ArgumentException
                      or InvalidDataException)
            {
                throw new InvalidDataException(
                    "A provider dispatch checkpoint has invalid wire "
                    + "evidence.",
                    exception);
            }
        }

        return new RecoveredProviderDispatch
        {
            ProviderId = runtimeEvent.ProviderId,
            ModelId = runtimeEvent.ModelId,
            TransportDialect = runtimeEvent.TransportDialect,
            ProviderCapabilityDigest =
                runtimeEvent.ProviderCapabilityDigest,
            ProviderRouteDigest = runtimeEvent.ProviderRouteDigest,
            ProviderRoutePolicyVersion = policyVersion,
            ProviderRoutePolicyDigest = policyDigest,
            ProviderDialectSemanticDigest = dialectSemanticDigest,
            ProviderDialectContract = dialectContract?.Snapshot(),
            ProviderAttemptId = runtimeEvent.AttemptId,
            StreamAttemptId = runtimeEvent.StreamAttemptId,
            TurnId = runtimeEvent.TurnId
        };
    }

    private sealed class RecoveredReceipt
    {
        public RecoveredReceipt(
            ActionReceipt receipt,
            string turnId,
            string? attemptId)
        {
            Receipt = receipt;
            TurnId = turnId;
            AttemptId = attemptId;
        }

        public ActionReceipt Receipt { get; }

        public string TurnId { get; }

        public string? AttemptId { get; }
    }

    private sealed class RecoveredToolCall
    {
        public RecoveredToolCall(
            string toolCallId,
            string toolName,
            string turnId,
            string? attemptId,
            DateTimeOffset timestamp)
        {
            ToolCallId = toolCallId;
            ToolName = toolName;
            TurnId = turnId;
            AttemptId = attemptId;
            Timestamp = timestamp;
        }

        public string ToolCallId { get; }

        public string ToolName { get; }

        public string TurnId { get; }

        public string? AttemptId { get; }

        public DateTimeOffset Timestamp { get; }
    }
}
