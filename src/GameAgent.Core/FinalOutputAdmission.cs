using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// The runtime-local tool used to submit a candidate final output when strict
/// final-output admission is enabled. The call is evaluated inside the agent
/// loop and is never dispatched to the game host.
/// </summary>
public static class FinalOutputAdmissionControl
{
    public const string SubmitToolName = "runtime_submit_final_output";

    public const string EvidencePresentationContentType =
        "application/vnd.game-agent.action-receipt-evidence+json";

    public const string PresentationExtensionName =
        "finalOutputPresentation";

    public const string PresentationContentType =
        "application/vnd.game-agent.final-output-presentation+json";

    /// <summary>
    /// Runtime-owned top-level tool-result property containing the exact
    /// durable source event ID that a later final-output submission may cite.
    /// It is presentation metadata and is never persisted inside the host's
    /// <see cref="ActionReceipt.Extensions"/>.
    /// </summary>
    public const string EvidenceSourceEventIdPropertyName =
        "finalOutputEvidenceSourceEventId";
}

/// <summary>
/// Bounded settings for strict final-output admission. Admission is disabled
/// by default so existing runtimes retain their completion behavior.
/// </summary>
public sealed class FinalOutputAdmissionOptions
{
    public bool Enabled { get; set; }

    public int MaxAttempts { get; set; } = 4;

    public int MaxOutputUtf8Bytes { get; set; } = 262_144;

    public int MaxEvidenceItems { get; set; } = 64;

    public int MaxEvidenceUtf8Bytes { get; set; } = 524_288;

    public int MaxJsonDepth { get; set; } = 32;

    public int MaxJsonNodes { get; set; } = 8_192;

    public int MaxPolicyFeedbackUtf8Bytes { get; set; } = 16_384;

    public int MaxConcurrentEvaluations { get; set; } = 4;

    public TimeSpan PolicyTimeout { get; set; } =
        TimeSpan.FromMilliseconds(500);

    internal void Validate()
    {
        if (MaxAttempts is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }

        if (MaxOutputUtf8Bytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutputUtf8Bytes));
        }

        if (MaxEvidenceItems is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEvidenceItems));
        }

        if (MaxEvidenceUtf8Bytes is < 1 or > 8_388_608)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEvidenceUtf8Bytes));
        }

        if (MaxJsonDepth is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxJsonDepth));
        }

        if (MaxJsonNodes is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxJsonNodes));
        }

        if (MaxPolicyFeedbackUtf8Bytes is < 1 or > 262_144)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPolicyFeedbackUtf8Bytes));
        }

        if (MaxConcurrentEvaluations is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentEvaluations));
        }

        if (PolicyTimeout < TimeSpan.FromMilliseconds(1)
            || PolicyTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(PolicyTimeout));
        }
    }

    internal FinalOutputAdmissionOptions Snapshot()
    {
        Validate();
        return new FinalOutputAdmissionOptions
        {
            Enabled = Enabled,
            MaxAttempts = MaxAttempts,
            MaxOutputUtf8Bytes = MaxOutputUtf8Bytes,
            MaxEvidenceItems = MaxEvidenceItems,
            MaxEvidenceUtf8Bytes = MaxEvidenceUtf8Bytes,
            MaxJsonDepth = MaxJsonDepth,
            MaxJsonNodes = MaxJsonNodes,
            MaxPolicyFeedbackUtf8Bytes = MaxPolicyFeedbackUtf8Bytes,
            MaxConcurrentEvaluations = MaxConcurrentEvaluations,
            PolicyTimeout = PolicyTimeout
        };
    }

    internal string Digest()
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "final-output-admission-options.v1");
        digest.Add("maxAttempts", MaxAttempts);
        digest.Add("maxOutputUtf8Bytes", MaxOutputUtf8Bytes);
        digest.Add("maxEvidenceItems", MaxEvidenceItems);
        digest.Add("maxEvidenceUtf8Bytes", MaxEvidenceUtf8Bytes);
        digest.Add("maxJsonDepth", MaxJsonDepth);
        digest.Add("maxJsonNodes", MaxJsonNodes);
        digest.Add(
            "maxPolicyFeedbackUtf8Bytes",
            MaxPolicyFeedbackUtf8Bytes);
        digest.Add("maxConcurrentEvaluations", MaxConcurrentEvaluations);
        digest.Add(
            "policyTimeoutTicks",
            PolicyTimeout.Ticks);
        return digest.Finish();
    }
}

/// <summary>
/// An immutable, bounded output schema bound to a durable run. The supported
/// schema subset is the same deterministic subset used for tool arguments.
/// </summary>
public sealed class FinalOutputContract
{
    private const string ContentType =
        "application/vnd.game-agent.final-output-contract+json";
    private static readonly ToolArgumentValidator SchemaValidator = new();
    private readonly JsonElement _schema;

    public FinalOutputContract(
        string schemaId,
        string schemaVersion,
        JsonElement schema)
    {
        SchemaId = RuntimeGuard.RequiredUtf8(
            schemaId,
            128,
            nameof(schemaId));
        SchemaVersion = RuntimeGuard.RequiredUtf8(
            schemaVersion,
            64,
            nameof(schemaVersion));
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "A final-output schema must be a JSON object.",
                nameof(schema));
        }

        JsonValueInspector.ValidateAndMeasure(
            schema,
            new JsonValueLimits(
                maxUtf8Bytes: 262_144,
                maxDepth: 128,
                maxNodes: 65_536,
                maxStringUtf8Bytes: 65_536,
                maxContainerItems: 8_192),
            nameof(schema));
        var validation = SchemaValidator.Validate(
            schema,
            JsonArrayBuilder.Null());
        var schemaError = validation.Errors.FirstOrDefault(
            error => error.Code.StartsWith(
                "schema_",
                StringComparison.Ordinal));
        if (schemaError is not null)
        {
            throw new ArgumentException(
                "The final-output schema uses an invalid or unsupported "
                + "schema construct: "
                + schemaError.Code
                + ".",
                nameof(schema));
        }

        _schema = schema.Clone();
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "final-output-contract.v1");
        digest.Add("schemaId", SchemaId);
        digest.Add("schemaVersion", SchemaVersion);
        digest.Add("schema", _schema);
        Digest = digest.Finish();
    }

    public string SchemaId { get; }

    public string SchemaVersion { get; }

    public string Digest { get; }

    public JsonElement Schema => _schema.Clone();

    internal FinalOutputContract Snapshot() =>
        new(SchemaId, SchemaVersion, _schema);

    internal bool Matches(FinalOutputContract other) =>
        other is not null
        && string.Equals(SchemaId, other.SchemaId, StringComparison.Ordinal)
        && string.Equals(
            SchemaVersion,
            other.SchemaVersion,
            StringComparison.Ordinal)
        && string.Equals(Digest, other.Digest, StringComparison.Ordinal);

    internal JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("contentType", JsonArrayBuilder.String(
                ContentType)),
            ("schemaId", JsonArrayBuilder.String(SchemaId)),
            ("schemaVersion", JsonArrayBuilder.String(SchemaVersion)),
            ("digest", JsonArrayBuilder.String(Digest)),
            ("schema", _schema));
    }

    internal static FinalOutputContract FromJson(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != 5
            || !TryString(value, "contentType", out var contentType)
            || !string.Equals(
                contentType,
                ContentType,
                StringComparison.Ordinal)
            || !TryString(value, "schemaId", out var schemaId)
            || !TryString(value, "schemaVersion", out var schemaVersion)
            || !TryString(value, "digest", out var digest)
            || !value.TryGetProperty("schema", out var schema))
        {
            throw new InvalidDataException(
                "A durable final-output contract is malformed.");
        }

        FinalOutputContract contract;
        try
        {
            contract = new FinalOutputContract(
                schemaId!,
                schemaVersion!,
                schema);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A durable final-output contract is invalid.",
                exception);
        }

        if (!string.Equals(
                contract.Digest,
                digest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A durable final-output contract digest is invalid.");
        }

        return contract;
    }

    private static bool TryString(
        JsonElement value,
        string name,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return !string.IsNullOrWhiteSpace(result);
    }
}

/// <summary>
/// Exact terminal host evidence available to an admission policy.
/// </summary>
public sealed class FinalOutputCommittedEvidence
{
    private readonly ActionReceipt _receipt;

    internal FinalOutputCommittedEvidence(
        string runId,
        string turnId,
        string sourceEventId,
        ActionReceipt receipt)
    {
        RunId = RuntimeGuard.RequiredId(runId, nameof(runId));
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        SourceEventId = RuntimeGuard.RequiredUtf8(
            sourceEventId,
            128,
            nameof(sourceEventId));
        _receipt = ProtocolJson.DeserializeActionReceipt(
            ProtocolJson.Serialize(receipt));
        if (_receipt.Status is not ReceiptStatuses.Succeeded
            and not ReceiptStatuses.Rejected
            and not ReceiptStatuses.Failed)
        {
            throw new ArgumentException(
                "Final-output evidence must contain a terminal receipt.",
                nameof(receipt));
        }
    }

    public string RunId { get; }

    public string TurnId { get; }

    public string SourceEventId { get; }

    public ActionReceipt Receipt => ProtocolJson.DeserializeActionReceipt(
        ProtocolJson.Serialize(_receipt));

    internal string Key =>
        _receipt.OperationId
        + "\0"
        + _receipt.Revision.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

    internal string ReceiptDigest =>
        CanonicalJsonDigest.ComputeSha256(ProtocolJson.ToElement(_receipt));
}

/// <summary>
/// Immutable structured proposal submitted by the model.
/// </summary>
public sealed class FinalOutputProposal
{
    private readonly JsonElement _output;
    private readonly IReadOnlyList<FinalOutputCommittedEvidence> _evidence;

    internal FinalOutputProposal(
        string toolCallId,
        string? assistantText,
        JsonElement output,
        IReadOnlyList<FinalOutputCommittedEvidence> evidence)
    {
        ToolCallId = RuntimeGuard.RequiredId(
            toolCallId,
            nameof(toolCallId));
        AssistantText = assistantText;
        _output = output.Clone();
        _evidence = new ReadOnlyCollection<FinalOutputCommittedEvidence>(
            evidence.ToArray());
    }

    public string ToolCallId { get; }

    public string? AssistantText { get; }

    public JsonElement Output => _output.Clone();

    public IReadOnlyList<FinalOutputCommittedEvidence> Evidence => _evidence;
}

/// <summary>
/// Immutable admission-policy input. All receipts are terminal, durable,
/// current-run snapshots. Context contains only the context selected for the
/// current provider turn.
/// </summary>
public sealed class FinalOutputAdmissionRequest
{
    private readonly AgentRun _run;
    private readonly IReadOnlyList<ContextCandidate> _context;
    private readonly IReadOnlyList<FinalOutputCommittedEvidence>
        _committedEvidence;

    internal FinalOutputAdmissionRequest(
        AgentRun run,
        string turnId,
        IReadOnlyList<ContextCandidate> context,
        FinalOutputProposal proposal,
        IReadOnlyList<FinalOutputCommittedEvidence> committedEvidence)
    {
        _run = JournalCoordinator.CloneRun(run);
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        _context = new ReadOnlyCollection<ContextCandidate>(
            context.Select(item => item.Clone()).ToArray());
        Proposal = proposal ?? throw new ArgumentNullException(
            nameof(proposal));
        _committedEvidence =
            new ReadOnlyCollection<FinalOutputCommittedEvidence>(
                committedEvidence.ToArray());
    }

    public AgentRun Run => JournalCoordinator.CloneRun(_run);

    public string TurnId { get; }

    public IReadOnlyList<ContextCandidate> Context => _context;

    public FinalOutputProposal Proposal { get; }

    public IReadOnlyList<FinalOutputCommittedEvidence> CommittedEvidence =>
        _committedEvidence;
}

public sealed class FinalOutputAdmissionDecision
{
    private FinalOutputAdmissionDecision(
        bool accepted,
        string reasonCode,
        JsonElement? feedback)
    {
        Accepted = accepted;
        ReasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
        Feedback = feedback?.Clone();
    }

    public bool Accepted { get; }

    public string ReasonCode { get; }

    public JsonElement? Feedback { get; }

    public static FinalOutputAdmissionDecision Accept(
        string reasonCode = "final_output_admitted") =>
        new(true, reasonCode, null);

    public static FinalOutputAdmissionDecision Reject(
        string reasonCode,
        JsonElement? feedback = null) =>
        new(false, reasonCode, feedback);
}

/// <summary>
/// Performs the business-specific final-output check. Implementations should
/// be deterministic for an identical request, honor cancellation, and avoid
/// mutating game state. The runtime enforces a wall-clock timeout and bounded
/// concurrency around this method.
/// </summary>
public interface IFinalOutputAdmissionPolicy
{
    string PolicyId { get; }

    string Version { get; }

    ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
        FinalOutputAdmissionRequest request,
        CancellationToken cancellationToken);
}

internal sealed class FinalOutputAdmissionEvaluator
{
    private readonly IFinalOutputAdmissionPolicy _policy;
    private readonly FinalOutputAdmissionOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly BoundedCancellationDispatcher _cancellationDispatcher;
    private readonly object _lifecycleSync = new();
    private readonly HashSet<Task> _detachedEvaluations = new();
    private TaskCompletionSource<bool>? _detachedEvaluationsDrained;
    private bool _stopped;

    public FinalOutputAdmissionEvaluator(
        IFinalOutputAdmissionPolicy policy,
        FinalOutputAdmissionOptions options,
        BoundedCancellationDispatcher? cancellationDispatcher = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options.Snapshot();
        PolicyId = RuntimeGuard.RequiredUtf8(
            policy.PolicyId,
            128,
            nameof(policy.PolicyId));
        PolicyVersion = RuntimeGuard.RequiredUtf8(
            policy.Version,
            64,
            nameof(policy.Version));
        _slots = new SemaphoreSlim(
            _options.MaxConcurrentEvaluations,
            _options.MaxConcurrentEvaluations);
        _cancellationDispatcher = cancellationDispatcher
                                  ?? BoundedCancellationDispatcher.Shared;
    }

    public string PolicyId { get; }

    public string PolicyVersion { get; }

    public string OptionsDigest => _options.Digest();

    internal int DetachedEvaluationCount
    {
        get
        {
            lock (_lifecycleSync)
            {
                return _detachedEvaluations.Count;
            }
        }
    }

    public async ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
        FinalOutputAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            if (_stopped)
            {
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_stopped");
            }
        }

        using var queueDeadline = new CancellationTokenSource(
            _options.PolicyTimeout);
        using var queueLinked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            queueDeadline.Token);
        try
        {
            await _slots.WaitAsync(queueLinked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return FinalOutputAdmissionDecision.Reject(
                "final_output_admission_capacity_timeout");
        }

        lock (_lifecycleSync)
        {
            if (_stopped)
            {
                _slots.Release();
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_stopped");
            }
        }

        BoundedCancellationDispatcher.CancellationDispatchReservation?
            cancellationReservation = null;
        CancellationTokenSource? policyCancellation = null;
        Task<FinalOutputAdmissionDecision>? evaluation = null;
        var releaseSlotHere = true;
        try
        {
            if (!_cancellationDispatcher.TryReserve(
                    out cancellationReservation))
            {
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_cancellation_capacity_exhausted");
            }

            policyCancellation = new CancellationTokenSource();
            try
            {
                evaluation = Task.Run(
                    async () => await _policy.EvaluateAsync(
                            request,
                            policyCancellation.Token)
                        .ConfigureAwait(false));
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_policy_error");
            }

            var timeout = Task.Delay(_options.PolicyTimeout);
            var callerCancellation = Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            var completed = await Task.WhenAny(
                    evaluation,
                    timeout,
                    callerCancellation)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, evaluation))
            {
                var cancellationDispatch = DispatchCancellation(
                    policyCancellation,
                    cancellationReservation!);
                var cancellationCleanup =
                    ObserveCancellationDispatchAsync(
                        cancellationDispatch,
                        cancellationReservation!);
                var detachedEvaluation = ReleaseWhenSettledAsync(
                    evaluation,
                    cancellationCleanup,
                    policyCancellation);
                TrackDetachedEvaluation(detachedEvaluation);
                policyCancellation = null;
                cancellationReservation = null;
                evaluation = null;
                releaseSlotHere = false;
                cancellationToken.ThrowIfCancellationRequested();
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_policy_timeout");
            }

            FinalOutputAdmissionDecision? decision;
            try
            {
                decision = await evaluation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested)
            {
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_policy_timeout");
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_policy_error");
            }

            if (decision is null)
            {
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_policy_result_invalid");
            }

            try
            {
                _ = RuntimeGuard.RequiredReasonCode(
                    decision.ReasonCode,
                    nameof(decision.ReasonCode));
                if (decision.Feedback.HasValue)
                {
                    JsonValueInspector.ValidateAndMeasure(
                        decision.Feedback.Value,
                        new JsonValueLimits(
                            maxUtf8Bytes:
                                _options.MaxPolicyFeedbackUtf8Bytes,
                            maxDepth: _options.MaxJsonDepth,
                            maxNodes: _options.MaxJsonNodes,
                            maxStringUtf8Bytes:
                                _options.MaxPolicyFeedbackUtf8Bytes,
                            maxContainerItems: _options.MaxJsonNodes),
                        nameof(decision.Feedback));
                }
            }
            catch (ArgumentException)
            {
                return FinalOutputAdmissionDecision.Reject(
                    "final_output_admission_policy_result_invalid");
            }

            return decision;
        }
        finally
        {
            if (releaseSlotHere)
            {
                policyCancellation?.Dispose();
                cancellationReservation?.Dispose();
                _slots.Release();
            }
        }
    }

    internal async ValueTask<bool> StopAsync()
    {
        Task drain;
        lock (_lifecycleSync)
        {
            _stopped = true;
            drain = _detachedEvaluations.Count == 0
                ? Task.CompletedTask
                : (_detachedEvaluationsDrained ??=
                    NewCompletion()).Task;
        }

        if (drain.IsCompleted)
        {
            await ObserveDrainAsync(drain).ConfigureAwait(false);
            return true;
        }

        var timeout = Task.Delay(_options.PolicyTimeout);
        var completed = await Task.WhenAny(drain, timeout)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, drain))
        {
            await ObserveDrainAsync(drain).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private void TrackDetachedEvaluation(Task detachedEvaluation)
    {
        lock (_lifecycleSync)
        {
            _detachedEvaluations.Add(detachedEvaluation);
        }

        _ = ObserveDetachedEvaluationAsync(detachedEvaluation);
    }

    private async Task ObserveDetachedEvaluationAsync(
        Task detachedEvaluation)
    {
        try
        {
            await detachedEvaluation.ConfigureAwait(false);
        }
        catch
        {
            // Detached policy and cancellation failures have already been
            // converted to a bounded admission result and remain isolated.
        }
        finally
        {
            TaskCompletionSource<bool>? drained = null;
            lock (_lifecycleSync)
            {
                _detachedEvaluations.Remove(detachedEvaluation);
                if (_detachedEvaluations.Count == 0)
                {
                    drained = _detachedEvaluationsDrained;
                    _detachedEvaluationsDrained = null;
                }
            }

            drained?.TrySetResult(true);
        }
    }

    private async Task ReleaseWhenSettledAsync(
        Task<FinalOutputAdmissionDecision> evaluation,
        Task cancellationCleanup,
        CancellationTokenSource policyCancellation)
    {
        try
        {
            try
            {
                await evaluation.ConfigureAwait(false);
            }
            catch
            {
                // The result was already failed closed when ownership moved
                // to detached cleanup.
            }

            try
            {
                await cancellationCleanup.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation dispatch failures are isolated and observed.
            }
        }
        finally
        {
            policyCancellation.Dispose();
            _slots.Release();
        }
    }

    private static async Task ObserveCancellationDispatchAsync(
        Task cancellationDispatch,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await cancellationDispatch.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation dispatch failures are isolated and observed.
        }
        finally
        {
            cancellationReservation.Dispose();
        }
    }

    private static Task DispatchCancellation(
        CancellationTokenSource policyCancellation,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            return cancellationReservation.DispatchAsync(
                policyCancellation);
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    private static async Task ObserveDrainAsync(Task drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch
        {
            // Detached evaluation failures are already observed by their
            // individual cleanup tasks.
        }
    }

    private static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class FinalOutputAdmissionBinding
{
    private const string ContentType =
        "application/vnd.game-agent.final-output-admission-binding+json";
    internal const string ExtensionName = "finalOutputAdmission";
    internal const string TurnSnapshotExtensionName =
        "finalOutputAdmissionBinding";
    internal const string ContractExtensionName = "finalOutputContract";

    public FinalOutputAdmissionBinding(
        string policyId,
        string policyVersion,
        string optionsDigest,
        FinalOutputContract? contract)
    {
        PolicyId = RuntimeGuard.RequiredUtf8(
            policyId,
            128,
            nameof(policyId));
        PolicyVersion = RuntimeGuard.RequiredUtf8(
            policyVersion,
            64,
            nameof(policyVersion));
        OptionsDigest = RuntimeGuard.RequiredUtf8(
            optionsDigest,
            256,
            nameof(optionsDigest));
        if (!CanonicalJsonDigest.IsSha256(OptionsDigest))
        {
            throw new ArgumentException(
                "Admission option digest must be a SHA-256 digest.",
                nameof(optionsDigest));
        }

        Contract = contract?.Snapshot();
    }

    public string PolicyId { get; }

    public string PolicyVersion { get; }

    public string OptionsDigest { get; }

    public FinalOutputContract? Contract { get; }

    public JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("contentType", JsonArrayBuilder.String(
                ContentType)),
            ("policyId", JsonArrayBuilder.String(PolicyId)),
            ("policyVersion", JsonArrayBuilder.String(PolicyVersion)),
            ("optionsDigest", JsonArrayBuilder.String(OptionsDigest)),
            ("contract", Contract is null
                ? JsonArrayBuilder.Null()
                : Contract.ToJson()));
    }

    public bool Matches(FinalOutputAdmissionBinding other)
    {
        return other is not null
               && string.Equals(
                   PolicyId,
                   other.PolicyId,
                   StringComparison.Ordinal)
               && string.Equals(
                   PolicyVersion,
                   other.PolicyVersion,
                   StringComparison.Ordinal)
               && string.Equals(
                   OptionsDigest,
                   other.OptionsDigest,
                   StringComparison.Ordinal)
               && (Contract is null && other.Contract is null
                   || Contract is not null
                   && other.Contract is not null
                   && Contract.Matches(other.Contract));
    }

    public static FinalOutputAdmissionBinding FromJson(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != 5
            || !TryString(value, "contentType", out var contentType)
            || !string.Equals(
                contentType,
                ContentType,
                StringComparison.Ordinal)
            || !TryString(value, "policyId", out var policyId)
            || !TryString(value, "policyVersion", out var policyVersion)
            || !TryString(value, "optionsDigest", out var optionsDigest)
            || !value.TryGetProperty("contract", out var contractValue))
        {
            throw new InvalidDataException(
                "A durable final-output admission binding is malformed.");
        }

        var contract = contractValue.ValueKind == JsonValueKind.Null
            ? null
            : FinalOutputContract.FromJson(contractValue);
        try
        {
            return new FinalOutputAdmissionBinding(
                policyId!,
                policyVersion!,
                optionsDigest!,
                contract);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A durable final-output admission binding is invalid.",
                exception);
        }
    }

    public static FinalOutputAdmissionBinding? Read(AgentRun run)
    {
        if (!run.Extensions.TryGetValue(ExtensionName, out var value))
        {
            return null;
        }

        return FromJson(value);
    }

    private static bool TryString(
        JsonElement value,
        string name,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return !string.IsNullOrWhiteSpace(result);
    }
}

internal sealed class FinalOutputEvidenceRegistry
{
    private readonly Dictionary<string, FinalOutputCommittedEvidence> _items =
        new(StringComparer.Ordinal);

    public int Count => _items.Count;

    public void Add(FinalOutputCommittedEvidence item)
    {
        if (!_items.TryAdd(item.Key, item))
        {
            throw new InvalidDataException(
                "Duplicate final-output evidence was observed.");
        }
    }

    public bool TryGet(
        string operationId,
        long revision,
        out FinalOutputCommittedEvidence? evidence)
    {
        return _items.TryGetValue(
            operationId
            + "\0"
            + revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            out evidence);
    }

    public IReadOnlyList<FinalOutputCommittedEvidence> Snapshot() =>
        _items.Values
            .OrderBy(
                item => item.Receipt.OperationId,
                StringComparer.Ordinal)
            .ThenBy(item => item.Receipt.Revision)
            .ToArray();
}

internal sealed class ParsedFinalOutputSubmission
{
    public ParsedFinalOutputSubmission(
        JsonElement output,
        IReadOnlyList<FinalOutputCommittedEvidence> evidence)
    {
        Output = output.Clone();
        Evidence = evidence;
    }

    public JsonElement Output { get; }

    public IReadOnlyList<FinalOutputCommittedEvidence> Evidence { get; }
}

internal static class FinalOutputAdmissionCodec
{
    internal const string ProvisionalPresentationState = "provisional";
    internal const string AdmittedPresentationState = "admitted";
    internal const string EvidenceExtensionName =
        "finalOutputAdmissionEvidence";
    internal const string FeedbackContentType =
        "application/vnd.game-agent.final-output-admission-result+json";

    public static JsonElement ForModelPresentation(
        ActionReceipt receipt,
        string sourceEventId)
    {
        var source = RuntimeGuard.RequiredUtf8(
            sourceEventId,
            128,
            nameof(sourceEventId));
        return JsonArrayBuilder.Object(
            ("contentType", JsonArrayBuilder.String(
                FinalOutputAdmissionControl
                    .EvidencePresentationContentType)),
            ("receipt", ProtocolJson.ToElement(receipt)),
            ("evidenceReference", JsonArrayBuilder.Object(
                ("operationId", JsonArrayBuilder.String(
                    receipt.OperationId)),
                ("revision", JsonArrayBuilder.Number(receipt.Revision)),
                (FinalOutputAdmissionControl
                    .EvidenceSourceEventIdPropertyName,
                    JsonArrayBuilder.String(source)))));
    }

    public static ToolDescriptor CreateSubmitDescriptor(
        FinalOutputAdmissionOptions options,
        FinalOutputContract? contract)
    {
        var outputSchema = contract?.Schema ?? EmptySchema();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            writer.WritePropertyName("output");
            outputSchema.WriteTo(writer);
            writer.WritePropertyName("evidence");
            writer.WriteStartObject();
            writer.WriteString("type", "array");
            writer.WriteNumber("maxItems", options.MaxEvidenceItems);
            writer.WritePropertyName("items");
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            WriteStringSchema(writer, "operationId", 128);
            writer.WritePropertyName("revision");
            writer.WriteStartObject();
            writer.WriteString("type", "integer");
            writer.WriteNumber("minimum", 0);
            writer.WriteEndObject();
            WriteStringSchema(writer, "sourceEventId", 128);
            writer.WriteEndObject();
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            writer.WriteStringValue("operationId");
            writer.WriteStringValue("revision");
            writer.WriteStringValue("sourceEventId");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            writer.WriteStringValue("output");
            writer.WriteStringValue("evidence");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new ToolDescriptor
        {
            Name = FinalOutputAdmissionControl.SubmitToolName,
            Version = "1",
            Description =
                "Submit the formal final output for deterministic runtime "
                + "admission. Put all user-visible output in `output`. Cite "
                + "only exact terminal evidence returned by game tools; "
                + "strict-mode game results use a typed wrapper whose "
                + "`evidenceReference` contains `operationId`, `revision`, "
                + "and `finalOutputEvidenceSourceEventId`. "
                + "This control never executes a game action and must be the "
                + "only tool call in its provider turn.",
            ParametersSchema = document.RootElement.Clone(),
            ResultSchema = FeedbackSchema(),
            Effect = ToolEffects.AgentLocalWrite,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = checked(
                (int)Math.Min(
                    int.MaxValue,
                    Math.Ceiling(options.PolicyTimeout.TotalMilliseconds))),
            RetryPolicy = ToolRetryPolicies.Never,
            IdempotencyPolicy = ToolIdempotencyPolicies.Required,
            Toolset = "runtime",
            Visibility = ToolVisibilities.Direct
        };
    }

    public static bool TryParseSubmission(
        ModelToolCall call,
        FinalOutputAdmissionOptions options,
        FinalOutputContract? contract,
        FinalOutputEvidenceRegistry registry,
        string runId,
        out ParsedFinalOutputSubmission? submission,
        out string reasonCode)
    {
        submission = null;
        reasonCode = "final_output_submission_invalid";
        try
        {
            JsonValueInspector.ValidateAndMeasure(
                call.Arguments,
                new JsonValueLimits(
                    maxUtf8Bytes: checked(
                        options.MaxOutputUtf8Bytes
                        + Math.Min(
                            options.MaxEvidenceUtf8Bytes,
                            1_048_576)),
                    maxDepth: options.MaxJsonDepth,
                    maxNodes: options.MaxJsonNodes,
                    maxStringUtf8Bytes: options.MaxOutputUtf8Bytes,
                    maxContainerItems: Math.Max(
                        options.MaxEvidenceItems,
                        16)),
                nameof(call.Arguments));
        }
        catch (ArgumentException)
        {
            reasonCode = "final_output_submission_bounds_exceeded";
            return false;
        }
        catch (OverflowException)
        {
            reasonCode = "final_output_submission_bounds_exceeded";
            return false;
        }

        if (call.Arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement? output = null;
        JsonElement? evidenceValue = null;
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in call.Arguments.EnumerateObject())
        {
            if (!properties.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "output":
                    output = property.Value.Clone();
                    break;
                case "evidence":
                    evidenceValue = property.Value.Clone();
                    break;
                default:
                    return false;
            }
        }

        if (!output.HasValue
            || !evidenceValue.HasValue
            || evidenceValue.Value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        try
        {
            JsonValueInspector.ValidateAndMeasure(
                output.Value,
                new JsonValueLimits(
                    maxUtf8Bytes: options.MaxOutputUtf8Bytes,
                    maxDepth: options.MaxJsonDepth,
                    maxNodes: options.MaxJsonNodes,
                    maxStringUtf8Bytes: options.MaxOutputUtf8Bytes,
                    maxContainerItems: options.MaxJsonNodes),
                "output");
        }
        catch (ArgumentException)
        {
            reasonCode = "final_output_output_bounds_exceeded";
            return false;
        }
        catch (OverflowException)
        {
            reasonCode = "final_output_output_bounds_exceeded";
            return false;
        }

        if (contract is not null)
        {
            var validation = new ToolArgumentValidator(
                new ToolArgumentValidationOptions(
                    argumentJsonLimits: new JsonValueLimits(
                        maxUtf8Bytes: options.MaxOutputUtf8Bytes,
                        maxDepth: options.MaxJsonDepth,
                        maxNodes: options.MaxJsonNodes,
                        maxStringUtf8Bytes: options.MaxOutputUtf8Bytes,
                        maxContainerItems: options.MaxJsonNodes)))
                .Validate(contract.Schema, output.Value);
            if (!validation.IsValid)
            {
                reasonCode = "final_output_schema_validation_failed";
                return false;
            }
        }

        if (evidenceValue.Value.GetArrayLength() > options.MaxEvidenceItems)
        {
            reasonCode = "final_output_evidence_count_exceeded";
            return false;
        }

        if (registry.Count > options.MaxEvidenceItems)
        {
            reasonCode = "final_output_committed_evidence_count_exceeded";
            return false;
        }

        var available = registry.Snapshot();
        var availableBytes = 0L;
        foreach (var item in available)
        {
            availableBytes = checked(
                availableBytes
                + Encoding.UTF8.GetByteCount(
                    ProtocolJson.Serialize(item.Receipt))
                + Encoding.UTF8.GetByteCount(item.SourceEventId));
        }

        if (availableBytes > options.MaxEvidenceUtf8Bytes)
        {
            reasonCode = "final_output_committed_evidence_bytes_exceeded";
            return false;
        }

        var cited = new List<FinalOutputCommittedEvidence>();
        var citedKeys = new HashSet<string>(StringComparer.Ordinal);
        long citedBytes = 0;
        foreach (var item in evidenceValue.Value.EnumerateArray())
        {
            if (!TryReadEvidenceReference(
                    item,
                    out var operationId,
                    out var revision,
                    out var sourceEventId))
            {
                reasonCode = "final_output_evidence_reference_invalid";
                return false;
            }

            var key = operationId
                      + "\0"
                      + revision.ToString(
                          System.Globalization.CultureInfo.InvariantCulture);
            if (!citedKeys.Add(key)
                || !registry.TryGet(
                    operationId!,
                    revision,
                    out var committed)
                || committed is null
                || !string.Equals(
                    committed.RunId,
                    runId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    committed.SourceEventId,
                    sourceEventId,
                    StringComparison.Ordinal))
            {
                reasonCode = "final_output_evidence_not_committed";
                return false;
            }

            citedBytes = checked(
                citedBytes
                + Encoding.UTF8.GetByteCount(
                    ProtocolJson.Serialize(committed.Receipt))
                + Encoding.UTF8.GetByteCount(committed.SourceEventId));
            if (citedBytes > options.MaxEvidenceUtf8Bytes)
            {
                reasonCode = "final_output_evidence_bytes_exceeded";
                return false;
            }

            cited.Add(committed);
        }

        submission = new ParsedFinalOutputSubmission(
            output.Value,
            cited);
        reasonCode = "final_output_submission_valid";
        return true;
    }

    public static JsonElement CreateResult(
        bool admitted,
        string reasonCode,
        int attempt,
        int maxAttempts,
        JsonElement? feedback = null)
    {
        return JsonArrayBuilder.Object(
            ("contentType", JsonArrayBuilder.String(FeedbackContentType)),
            ("admitted", JsonArrayBuilder.Boolean(admitted)),
            ("reasonCode", JsonArrayBuilder.String(reasonCode)),
            ("attempt", JsonArrayBuilder.Number(attempt)),
            ("remainingAttempts", JsonArrayBuilder.Number(
                Math.Max(0, maxAttempts - attempt))),
            ("feedback", feedback?.Clone() ?? JsonArrayBuilder.Null()));
    }

    public static JsonElement CreatePresentation(
        string state,
        string reasonCode,
        JsonElement? admissionEvidence = null)
    {
        if (state is not ProvisionalPresentationState
            and not AdmittedPresentationState)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        var reason = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
        if ((state == ProvisionalPresentationState
             && admissionEvidence.HasValue)
            || (state == AdmittedPresentationState
                && !admissionEvidence.HasValue))
        {
            throw new ArgumentException(
                "Presentation evidence must exist only for admitted output.",
                nameof(admissionEvidence));
        }

        return JsonArrayBuilder.Object(
            ("contentType", JsonArrayBuilder.String(
                FinalOutputAdmissionControl.PresentationContentType)),
            ("state", JsonArrayBuilder.String(state)),
            ("reasonCode", JsonArrayBuilder.String(reason)),
            ("evidenceDigest", admissionEvidence.HasValue
                ? JsonArrayBuilder.String(
                    CanonicalJsonDigest.ComputeSha256(
                        admissionEvidence.Value))
                : JsonArrayBuilder.Null()));
    }

    public static string ValidatePresentation(
        JsonElement value,
        JsonElement? admissionEvidence)
    {
        var state = ReadPresentationState(value);
        var evidenceDigest = value.GetProperty("evidenceDigest");
        if (state == ProvisionalPresentationState)
        {
            if (admissionEvidence.HasValue)
            {
                throw new InvalidDataException(
                    "Provisional output has invalid admission evidence.");
            }
        }
        else if (!admissionEvidence.HasValue
                 || !string.Equals(
                     evidenceDigest.GetString(),
                     CanonicalJsonDigest.ComputeSha256(
                         admissionEvidence.Value),
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Admitted output has invalid admission evidence.");
        }

        return state;
    }

    public static string ReadPresentationState(JsonElement value)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            new JsonValueLimits(
                maxUtf8Bytes: 2_048,
                maxDepth: 4,
                maxNodes: 16,
                maxStringUtf8Bytes: 512,
                maxContainerItems: 8),
            "finalOutputPresentation");
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != 4
            || !ReadString(value, "contentType", out var contentType)
            || !ReadString(value, "state", out var state)
            || !ReadString(value, "reasonCode", out _)
            || !value.TryGetProperty(
                "evidenceDigest",
                out var evidenceDigest)
            || !string.Equals(
                contentType,
                FinalOutputAdmissionControl.PresentationContentType,
                StringComparison.Ordinal)
            || state is not ProvisionalPresentationState
                and not AdmittedPresentationState)
        {
            throw new InvalidDataException(
                "Final-output presentation metadata is malformed.");
        }

        if (state == ProvisionalPresentationState)
        {
            if (evidenceDigest.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidDataException(
                    "Provisional output has invalid admission evidence.");
            }
        }
        else if (evidenceDigest.ValueKind != JsonValueKind.String
                 || !CanonicalJsonDigest.IsSha256(
                     evidenceDigest.GetString()))
        {
            throw new InvalidDataException(
                "Admitted output has invalid admission evidence.");
        }

        return state!;
    }

    public static bool IsCommittedAttempt(
        NormalizedMessage assistant,
        JsonElement presentation)
    {
        if (assistant is null)
        {
            throw new ArgumentNullException(nameof(assistant));
        }

        if (!string.Equals(
                assistant.Role,
                NormalizedRoles.Assistant,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Final-output presentation metadata is not bound to an "
                + "assistant message.");
        }

        var state = ReadPresentationState(presentation);
        var reasonCode = presentation
            .GetProperty("reasonCode")
            .GetString()!;
        var toolCalls = assistant.Parts
            .Where(
                part => string.Equals(
                    part.Type,
                    NormalizedPartTypes.ToolCall,
                    StringComparison.Ordinal))
            .ToArray();
        var submitCalls = toolCalls.Count(
            part => string.Equals(
                part.ToolName,
                FinalOutputAdmissionControl.SubmitToolName,
                StringComparison.Ordinal));

        if (string.Equals(
                state,
                AdmittedPresentationState,
                StringComparison.Ordinal))
        {
            if (toolCalls.Length != 1 || submitCalls != 1)
            {
                throw new InvalidDataException(
                    "Admitted final-output presentation metadata has no "
                    + "exclusive submission call.");
            }

            return true;
        }

        if (submitCalls > 0)
        {
            return true;
        }

        if (string.Equals(
                reasonCode,
                "final_output_submission_required",
                StringComparison.Ordinal))
        {
            if (toolCalls.Length != 0)
            {
                throw new InvalidDataException(
                    "Missing-submission presentation metadata contains "
                    + "tool calls.");
            }

            return true;
        }

        if (string.Equals(
                reasonCode,
                "provider_follow_up",
                StringComparison.Ordinal))
        {
            if (toolCalls.Length != 0)
            {
                throw new InvalidDataException(
                    "Provider follow-up presentation metadata contains "
                    + "tool calls.");
            }

            return false;
        }

        if (string.Equals(
                reasonCode,
                "final_output_not_submitted",
                StringComparison.Ordinal))
        {
            if (toolCalls.Length == 0)
            {
                throw new InvalidDataException(
                    "Non-submission presentation metadata contains no "
                    + "tool call.");
            }

            return false;
        }

        throw new InvalidDataException(
            "Provisional final-output presentation metadata has no "
            + "matching runtime-owned attempt.");
    }

    public static JsonElement CreateEvidence(
        AgentRun run,
        string turnId,
        NormalizedMessage assistant,
        FinalOutputProposal proposal,
        FinalOutputAdmissionBinding binding,
        string decisionReasonCode)
    {
        var assistantDigest = CanonicalJsonDigest.ComputeSha256(
            NormalizedMessageJournalCodec.Encode(assistant));
        var outputDigest = CanonicalJsonDigest.ComputeSha256(
            proposal.Output);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.final-output-admission-evidence+json");
            writer.WriteString("runId", run.RunId);
            writer.WriteString("turnId", turnId);
            writer.WriteString("toolCallId", proposal.ToolCallId);
            writer.WriteString("policyId", binding.PolicyId);
            writer.WriteString("policyVersion", binding.PolicyVersion);
            writer.WriteString("optionsDigest", binding.OptionsDigest);
            if (binding.Contract is not null)
            {
                writer.WriteString(
                    "contractDigest",
                    binding.Contract.Digest);
            }

            writer.WriteString("decisionReasonCode", decisionReasonCode);
            writer.WriteString("assistantDigest", assistantDigest);
            writer.WriteString("outputDigest", outputDigest);
            writer.WritePropertyName("evidence");
            writer.WriteStartArray();
            foreach (var item in proposal.Evidence.OrderBy(
                         value => value.Receipt.OperationId,
                         StringComparer.Ordinal)
                     .ThenBy(value => value.Receipt.Revision))
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "operationId",
                    item.Receipt.OperationId);
                writer.WriteNumber("revision", item.Receipt.Revision);
                writer.WriteString("sourceEventId", item.SourceEventId);
                writer.WriteString("receiptDigest", item.ReceiptDigest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static void ValidateEvidence(
        JsonElement value,
        AgentRun run,
        string turnId,
        NormalizedMessage assistant,
        JsonElement finalOutput,
        IReadOnlyList<FinalOutputCommittedEvidence> committedEvidence)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            new JsonValueLimits(
                maxUtf8Bytes: 1_048_576,
                maxDepth: 32,
                maxNodes: 16_384,
                maxStringUtf8Bytes: 262_144,
                maxContainerItems: 4_096),
            "finalOutputAdmissionEvidence");
        var binding = FinalOutputAdmissionBinding.Read(run)
                      ?? throw new InvalidDataException(
                          "Final-output admission evidence has no durable "
                          + "run binding.");
        var expectedPropertyCount =
            binding.Contract is null ? 11 : 12;
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != expectedPropertyCount
            || !ReadString(value, "contentType", out var contentType)
            || !ReadString(value, "runId", out var evidenceRunId)
            || !ReadString(value, "turnId", out var evidenceTurnId)
            || !ReadString(value, "toolCallId", out var toolCallId)
            || !ReadString(value, "policyId", out var policyId)
            || !ReadString(value, "policyVersion", out var policyVersion)
            || !ReadString(value, "optionsDigest", out var optionsDigest)
            || !ReadString(
                value,
                "decisionReasonCode",
                out var decisionReason)
            || !ReadString(value, "assistantDigest", out var assistantDigest)
            || !ReadString(value, "outputDigest", out var outputDigest)
            || !value.TryGetProperty("evidence", out var cited)
            || cited.ValueKind != JsonValueKind.Array
            || !string.Equals(
                contentType,
                "application/vnd.game-agent.final-output-admission-evidence+json",
                StringComparison.Ordinal)
            || !string.Equals(
                evidenceRunId,
                run.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                evidenceTurnId,
                turnId,
                StringComparison.Ordinal)
            || !string.Equals(
                policyId,
                binding.PolicyId,
                StringComparison.Ordinal)
            || !string.Equals(
                policyVersion,
                binding.PolicyVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                optionsDigest,
                binding.OptionsDigest,
                StringComparison.Ordinal)
            || !CanonicalJsonDigest.IsSha256(assistantDigest)
            || !CanonicalJsonDigest.IsSha256(outputDigest)
            || string.IsNullOrWhiteSpace(decisionReason))
        {
            throw new InvalidDataException(
                "Final-output admission evidence is malformed.");
        }

        try
        {
            _ = RuntimeGuard.RequiredReasonCode(
                decisionReason!,
                "decisionReasonCode");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Final-output admission evidence has an invalid decision "
                + "reason.",
                exception);
        }

        if (binding.Contract is null)
        {
            if (value.TryGetProperty("contractDigest", out _))
            {
                throw new InvalidDataException(
                    "Final-output admission evidence cites an unexpected "
                    + "output contract.");
            }
        }
        else if (!ReadString(
                     value,
                     "contractDigest",
                     out var contractDigest)
                 || !string.Equals(
                     contractDigest,
                     binding.Contract.Digest,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Final-output admission evidence has an invalid output "
                + "contract digest.");
        }

        var actualAssistantDigest = CanonicalJsonDigest.ComputeSha256(
            NormalizedMessageJournalCodec.Encode(assistant));
        var actualOutputDigest =
            CanonicalJsonDigest.ComputeSha256(finalOutput);
        if (!string.Equals(
                assistantDigest,
                actualAssistantDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                outputDigest,
                actualOutputDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Final-output admission evidence does not match the "
                + "committed assistant output.");
        }

        var toolCalls = assistant.Parts
            .Where(
                part => string.Equals(
                    part.Type,
                    NormalizedPartTypes.ToolCall,
                    StringComparison.Ordinal))
            .ToArray();
        var admissionCall = toolCalls.Length == 1
            && string.Equals(
                toolCalls[0].ToolName,
                FinalOutputAdmissionControl.SubmitToolName,
                StringComparison.Ordinal)
                ? toolCalls[0]
                : null;
        var submittedArguments = admissionCall?.Json;
        if (admissionCall is null
            || !string.Equals(
                admissionCall.ToolCallId,
                toolCallId,
                StringComparison.Ordinal)
            || !submittedArguments.HasValue
            || submittedArguments.Value.ValueKind != JsonValueKind.Object
            || submittedArguments.Value.EnumerateObject().Count() != 2
            || !submittedArguments.Value.TryGetProperty(
                "output",
                out var submittedOutput)
            || !submittedArguments.Value.TryGetProperty(
                "evidence",
                out var submittedEvidence)
            || submittedEvidence.ValueKind != JsonValueKind.Array
            || !string.Equals(
                CanonicalJsonDigest.ComputeSha256(submittedOutput),
                actualOutputDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Final-output admission evidence does not match its "
                + "runtime-local submission.");
        }

        var available = committedEvidence.ToDictionary(
            item => item.Key,
            StringComparer.Ordinal);
        var extensionReferences = ValidateEvidenceArray(
            cited,
            available,
            requireReceiptDigest: true);
        var submissionReferences = ValidateEvidenceArray(
            submittedEvidence,
            available,
            requireReceiptDigest: false);
        if (extensionReferences.Count != submissionReferences.Count
            || extensionReferences.Except(
                    submissionReferences,
                    StringComparer.Ordinal)
                .Any())
        {
            throw new InvalidDataException(
                "Final-output admission evidence does not match the "
                + "submitted evidence references.");
        }
    }

    public static string ComputeToolDigest(
        IEnumerable<ToolDescriptor> tools)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "effective_direct_tools");
        foreach (var tool in tools.OrderBy(
                     value => value.Name,
                     StringComparer.Ordinal))
        {
            digest.Add("name", tool.Name);
            digest.Add("version", tool.Version);
            digest.Add("descriptor", ProtocolJson.ToElement(tool));
        }

        return digest.Finish();
    }

    private static HashSet<string> ValidateEvidenceArray(
        JsonElement value,
        IReadOnlyDictionary<string, FinalOutputCommittedEvidence> available,
        bool requireReceiptDigest)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (!TryReadEvidenceReference(
                    item,
                    out var operationId,
                    out var revision,
                    out var sourceEventId,
                    allowReceiptDigest: requireReceiptDigest))
            {
                throw new InvalidDataException(
                    "A final-output evidence reference is malformed.");
            }

            var key = operationId
                      + "\0"
                      + revision.ToString(
                          System.Globalization.CultureInfo.InvariantCulture);
            if (!result.Add(key)
                || !available.TryGetValue(key, out var committed)
                || !string.Equals(
                    sourceEventId,
                    committed.SourceEventId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Final-output evidence cites an uncommitted receipt.");
            }

            if (requireReceiptDigest
                && (!ReadString(
                        item,
                        "receiptDigest",
                        out var receiptDigest)
                    || !string.Equals(
                        receiptDigest,
                        committed.ReceiptDigest,
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "A final-output receipt digest is invalid.");
            }
        }

        return result;
    }

    private static bool ReadString(
        JsonElement value,
        string name,
        out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return !string.IsNullOrWhiteSpace(result);
    }

    private static bool TryReadEvidenceReference(
        JsonElement value,
        out string? operationId,
        out long revision,
        out string? sourceEventId,
        bool allowReceiptDigest = false)
    {
        operationId = null;
        sourceEventId = null;
        revision = -1;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "operationId"
                    when property.Value.ValueKind == JsonValueKind.String:
                    operationId = property.Value.GetString();
                    break;
                case "revision" when property.Value.TryGetInt64(out var parsed):
                    revision = parsed;
                    break;
                case "sourceEventId"
                    when property.Value.ValueKind == JsonValueKind.String:
                    sourceEventId = property.Value.GetString();
                    break;
                case "receiptDigest"
                    when allowReceiptDigest
                         && property.Value.ValueKind
                         == JsonValueKind.String:
                    break;
                default:
                    return false;
            }
        }

        return revision >= 0
               && IsBounded(operationId, 128)
               && IsBounded(sourceEventId, 128);
    }

    private static bool IsBounded(string? value, int bytes) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= bytes;

    private static void WriteStringSchema(
        Utf8JsonWriter writer,
        string name,
        int maximumLength)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteNumber("minLength", 1);
        writer.WriteNumber("maxLength", maximumLength);
        writer.WriteEndObject();
    }

    private static JsonElement EmptySchema()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement FeedbackSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "contentType": { "type": "string" },
                "admitted": { "type": "boolean" },
                "reasonCode": { "type": "string" },
                "attempt": { "type": "integer", "minimum": 1 },
                "remainingAttempts": { "type": "integer", "minimum": 0 },
                "feedback": {}
              },
              "required": [
                "contentType",
                "admitted",
                "reasonCode",
                "attempt",
                "remainingAttempts",
                "feedback"
              ],
              "additionalProperties": false
            }
            """);
        return document.RootElement.Clone();
    }
}
