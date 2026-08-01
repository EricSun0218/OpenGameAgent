using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// Creates an owned immutable-by-convention copy of a mutable public run
/// request before an orchestration layer performs asynchronous admission.
/// The durable runtime still applies its configured, stricter validation.
/// </summary>
public static class DurableRunRequestSnapshotter
{
    private const int MaxHeadlessItems = 512;
    private const int MaxHeadlessInputUtf8Bytes = 1_048_576;
    private const int MaxCompletionMessages = 4_096;
    private const int MaxTranscriptMessages = 65_536;
    private const int MaxTranscriptUtf8Bytes = 64 * 1_048_576;
    private static readonly JsonValueLimits RunJsonLimits = new();
    private static readonly JsonValueLimits WorkflowInputLimits = new(
        maxUtf8Bytes: 1_048_576,
        maxDepth: 64,
        maxNodes: 65_536,
        maxStringUtf8Bytes: 262_144,
        maxContainerItems: 16_384);

    public static DurableRunRequest Snapshot(
        DurableRunRequest source,
        CancellationToken cancellationToken) =>
        SnapshotDurableRunRequest(
            source,
            cancellationToken,
            validateAgentRun: true);

    /// <summary>
    /// Creates the same bounded owned snapshot while preserving a custom
    /// backend's authority to validate the semantic completeness of its run.
    /// </summary>
    public static DurableRunRequest SnapshotForBackendBoundary(
        DurableRunRequest source,
        CancellationToken cancellationToken) =>
        SnapshotDurableRunRequest(
            source,
            cancellationToken,
            validateAgentRun: false);

    private static DurableRunRequest SnapshotDurableRunRequest(
        DurableRunRequest source,
        CancellationToken cancellationToken,
        bool validateAgentRun)
    {
        if (source is null || source.Run is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var contextInput = RuntimeInputGuard.CopyBounded(
            source.Context
            ?? throw new ArgumentException(
                "A durable run request requires a context collection.",
                nameof(source)),
            DurableRunInputJournalCodec.MaxContextCandidates,
            candidate => candidate
                         ?? throw new ArgumentException(
                             "Context collections cannot contain null entries.",
                             nameof(source)),
            nameof(source.Context),
            "context_candidate_count_exceeded",
            cancellationToken);
        var activeSkillInput = RuntimeInputGuard.CopyBounded(
            source.ActiveSkills
            ?? throw new ArgumentException(
                "A durable run request requires an active-skill collection.",
                nameof(source)),
            DurableRunInputJournalCodec.MaxActiveSkills,
            skill => skill
                     ?? throw new ArgumentException(
                         "Active-skill collections cannot contain null entries.",
                         nameof(source)),
            nameof(source.ActiveSkills),
            "activated_skill_count_exceeded",
            cancellationToken);
        var encodedInput = DurableRunInputJournalCodec.Encode(
            contextInput,
            activeSkillInput,
            source.WorkloadClass,
            source.ExecutionMode,
            source.Inference,
            source.RoutePreference);
        var input = DurableRunInputJournalCodec.Decode(encodedInput);
        var transcriptInput = RuntimeInputGuard.CopyBounded(
            source.InitialTranscript
            ?? throw new ArgumentException(
                "A durable run request requires a transcript collection.",
                nameof(source)),
            MaxTranscriptMessages,
            item => item
                    ?? throw new ArgumentException(
                        "Transcript collections cannot contain null entries.",
                        nameof(source)),
            nameof(source.InitialTranscript),
            "prompt_message_count_exceeded",
            cancellationToken);
        _ = RuntimePromptBuilder.MeasurePrompt(
            transcriptInput,
            Array.Empty<GameAgent.Protocol.ToolDescriptor>(),
            MaxTranscriptMessages,
            MaxTranscriptUtf8Bytes,
            estimatedBytesPerToken: 4,
            ScriptAwareTokenEstimator.Shared);
        var transcript = RuntimeInputGuard.CopyBounded(
            transcriptInput,
            MaxTranscriptMessages,
            item => item is null
                ? throw new ArgumentException(
                    "Transcript collections cannot contain null entries.",
                    nameof(source))
                : NormalizedMessageJournalCodec.CloneValidated(
                    item,
                    cancellationToken),
            nameof(source.InitialTranscript),
            "prompt_message_count_exceeded",
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var run = validateAgentRun
            ? Snapshot(source.Run, cancellationToken)
            : SnapshotForBackendBoundary(source.Run, cancellationToken);

        return new DurableRunRequest
        {
            Run = run,
            Context = input.Context,
            ActiveSkills = input.ActiveSkills,
            InitialTranscript = transcript,
            LaneId = source.LaneId,
            WorkloadClass = input.WorkloadClass,
            ExecutionMode = input.ExecutionMode,
            Inference = input.Inference,
            RoutePreference = input.RoutePreference,
            FinalOutputContract = source.FinalOutputContract?.Snapshot()
        };
    }

    public static HeadlessRunRequest Snapshot(
        HeadlessRunRequest source,
        CancellationToken cancellationToken) =>
        SnapshotHeadlessRunRequest(
            source,
            cancellationToken,
            validateAgentRun: true);

    /// <summary>
    /// Creates a bounded headless snapshot without imposing the core
    /// runtime's AgentRun semantic validation on an injected backend.
    /// </summary>
    public static HeadlessRunRequest SnapshotForBackendBoundary(
        HeadlessRunRequest source,
        CancellationToken cancellationToken) =>
        SnapshotHeadlessRunRequest(
            source,
            cancellationToken,
            validateAgentRun: false);

    private static HeadlessRunRequest SnapshotHeadlessRunRequest(
        HeadlessRunRequest source,
        CancellationToken cancellationToken,
        bool validateAgentRun)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        if (source.Run is null)
        {
            throw new ArgumentException(
                "A headless run request requires a run.",
                nameof(source));
        }

        long inputBytes = 0;
        var observations = RuntimeInputGuard.CopyBounded(
            source.Observations
            ?? throw new ArgumentException(
                "A headless run request requires an observation collection.",
                nameof(source)),
            MaxHeadlessItems,
            observation =>
            {
                var safe = RuntimeProtocolInputGuard
                    .ValidateObservationBeforeSerialization(
                        observation
                        ?? throw new ArgumentException(
                            "Observation collections cannot contain null entries.",
                            nameof(source)),
                        RunJsonLimits,
                        RunJsonLimits.MaxUtf8Bytes,
                        nameof(source.Observations));
                var encoded = ProtocolJson.ToElement(safe);
                ChargeHeadlessInput(
                    ref inputBytes,
                    JsonValueInspector.ValidateAndMeasure(
                        encoded,
                        RunJsonLimits,
                        nameof(source.Observations)));
                return ProtocolJson.DeserializeObservationEnvelope(
                    encoded.GetRawText());
            },
            nameof(source.Observations),
            "observation_count_exceeded",
            cancellationToken);
        var tools = RuntimeInputGuard.CopyBounded(
            source.Tools
            ?? throw new ArgumentException(
                "A headless run request requires a tool collection.",
                nameof(source)),
            MaxHeadlessItems,
            tool =>
            {
                var safe = RuntimeProtocolInputGuard
                    .ValidateToolBeforeSerialization(
                        tool
                        ?? throw new ArgumentException(
                            "Tool collections cannot contain null entries.",
                            nameof(source)),
                        RunJsonLimits,
                        RunJsonLimits.MaxUtf8Bytes,
                        nameof(source.Tools));
                var encoded = ProtocolJson.ToElement(safe);
                ChargeHeadlessInput(
                    ref inputBytes,
                    JsonValueInspector.ValidateAndMeasure(
                        encoded,
                        RunJsonLimits,
                        nameof(source.Tools)));
                return ProtocolJson.DeserializeToolDescriptor(
                    encoded.GetRawText());
            },
            nameof(source.Tools),
            "tool_count_exceeded",
            cancellationToken);
        var run = validateAgentRun
            ? Snapshot(source.Run, cancellationToken)
            : SnapshotForBackendBoundary(source.Run, cancellationToken);
        ChargeHeadlessInput(
            ref inputBytes,
            JsonValueInspector.ValidateAndMeasure(
                ProtocolJson.ToElement(run),
                RunJsonLimits,
                nameof(source.Run)));
        return new HeadlessRunRequest
        {
            Run = run,
            Observations = observations,
            Tools = tools
        };

        static void ChargeHeadlessInput(ref long total, int bytes)
        {
            if (bytes > MaxHeadlessInputUtf8Bytes - total)
            {
                throw new RuntimeContentLimitException(
                    nameof(source),
                    "headless_input_bytes_exceeded",
                    "The headless run input exceeds its byte limit.");
            }

            total += bytes;
        }
    }

    public static AgentRun Snapshot(
        AgentRun source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var safe = RuntimeProtocolInputGuard
            .ValidateAgentRunBeforeSerialization(
                source,
                RunJsonLimits,
                RunJsonLimits.MaxUtf8Bytes,
                nameof(source));
        var encoded = ProtocolJson.ToElement(safe);
        JsonValueInspector.ValidateAndMeasure(
            encoded,
            RunJsonLimits,
            nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        return safe;
    }

    /// <summary>
    /// Clones and bounds a run for an injected backend without deciding
    /// whether the backend accepts a semantically incomplete run model.
    /// </summary>
    public static AgentRun SnapshotForBackendBoundary(
        AgentRun source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var safe = RuntimeProtocolInputGuard
            .SnapshotAgentRunForBackendBoundaryBeforeSerialization(
                source,
                RunJsonLimits,
                RunJsonLimits.MaxUtf8Bytes,
                nameof(source));
        var encoded = ProtocolJson.ToElement(safe);
        JsonValueInspector.ValidateAndMeasure(
            encoded,
            RunJsonLimits,
            nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        return safe;
    }

    public static RoutedExecutionRequest Snapshot(
        RoutedExecutionRequest source,
        CancellationToken cancellationToken) =>
        SnapshotRoutedExecutionRequest(
            source,
            cancellationToken,
            validateAgentRun: true);

    /// <summary>
    /// Creates a bounded routed snapshot while leaving run semantic
    /// validation to the injected backend selected by the route.
    /// </summary>
    public static RoutedExecutionRequest SnapshotForBackendBoundary(
        RoutedExecutionRequest source,
        CancellationToken cancellationToken) =>
        SnapshotRoutedExecutionRequest(
            source,
            cancellationToken,
            validateAgentRun: false);

    private static RoutedExecutionRequest SnapshotRoutedExecutionRequest(
        RoutedExecutionRequest source,
        CancellationToken cancellationToken,
        bool validateAgentRun)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var route = ExecutionRouteValidation.Snapshot(source.Route);
        var run = source.Run is null
            ? null
            : validateAgentRun
                ? Snapshot(source.Run, cancellationToken)
                : SnapshotForBackendBoundary(
                    source.Run,
                    cancellationToken);
        RoutedWorkflowRequest? workflow = null;
        if (source.Workflow is not null)
        {
            var input = source.Workflow.Input;
            JsonValueInspector.ValidateAndMeasure(
                input,
                WorkflowInputLimits,
                nameof(source.Workflow.Input));
            workflow = new RoutedWorkflowRequest
            {
                WorkflowId = RuntimeGuard.RequiredUtf8(
                    source.Workflow.WorkflowId,
                    128,
                    nameof(source.Workflow.WorkflowId)),
                RunKey = RuntimeGuard.RequiredUtf8(
                    source.Workflow.RunKey,
                    256,
                    nameof(source.Workflow.RunKey)),
                OwnerId = RuntimeGuard.RequiredUtf8(
                    source.Workflow.OwnerId,
                    256,
                    nameof(source.Workflow.OwnerId)),
                Input = input.Clone()
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new RoutedExecutionRequest
        {
            Route = route,
            Run = run,
            Workflow = workflow
        };
    }

    public static SimpleCompletionRequest Snapshot(
        SimpleCompletionRequest source,
        CancellationToken cancellationToken) =>
        SnapshotSimpleCompletionRequest(
            source,
            cancellationToken,
            requireMessages: true);

    /// <summary>
    /// Creates a bounded completion snapshot while preserving an injected
    /// backend's option to support an empty message collection.
    /// </summary>
    public static SimpleCompletionRequest SnapshotForBackendBoundary(
        SimpleCompletionRequest source,
        CancellationToken cancellationToken) =>
        SnapshotSimpleCompletionRequest(
            source,
            cancellationToken,
            requireMessages: false);

    private static SimpleCompletionRequest SnapshotSimpleCompletionRequest(
        SimpleCompletionRequest source,
        CancellationToken cancellationToken,
        bool requireMessages)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        var input = RuntimeInputGuard.CopyBounded(
            source.Messages
            ?? throw new ArgumentException(
                "A simple completion requires a message collection.",
                nameof(source)),
            MaxCompletionMessages,
            message => message
                       ?? throw new ArgumentException(
                           "Completion messages cannot contain null entries.",
                           nameof(source)),
            nameof(source.Messages),
            "completion_message_count_exceeded",
            cancellationToken);
        if (requireMessages && input.Length == 0)
        {
            throw new ArgumentException(
                "A simple completion requires at least one message.",
                nameof(source));
        }

        _ = RuntimePromptBuilder.MeasurePrompt(
            input,
            Array.Empty<ToolDescriptor>(),
            MaxCompletionMessages,
            MaxTranscriptUtf8Bytes,
            estimatedBytesPerToken: 4,
            ScriptAwareTokenEstimator.Shared);
        var messages = RuntimeInputGuard.CopyBounded(
            input,
            MaxCompletionMessages,
            message => NormalizedMessageJournalCodec.CloneValidated(
                message,
                cancellationToken),
            nameof(source.Messages),
            "completion_message_count_exceeded",
            cancellationToken);
        if (source.EstimatedPromptTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source.EstimatedPromptTokens));
        }

        if (source.MaxOutputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(source.MaxOutputTokens));
        }

        return new SimpleCompletionRequest
        {
            OperationId = source.OperationId is null
                ? null
                : RuntimeGuard.RequiredId(
                    source.OperationId,
                    nameof(source.OperationId)),
            Messages = messages,
            WorkloadClass = ProviderWorkloadClasses.Normalize(
                source.WorkloadClass,
                nameof(source.WorkloadClass)),
            EstimatedPromptTokens = source.EstimatedPromptTokens,
            MaxOutputTokens = source.MaxOutputTokens,
            Inference = source.Inference?.CloneValidated(),
            RoutePreference = source.RoutePreference?.CloneValidated()
        };
    }

    public static DurableRunContinuation? Snapshot(
        DurableRunContinuation? source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return null;
        }

        var context = RuntimeInputGuard.CopyBounded(
            source.Context
            ?? throw new ArgumentException(
                "A durable continuation requires a context collection.",
                nameof(source)),
            DurableRunInputJournalCodec.MaxContextCandidates,
            candidate => (candidate
                          ?? throw new ArgumentException(
                              "Context collections cannot contain null entries.",
                              nameof(source))).Clone(),
            nameof(source.Context),
            "context_candidate_count_exceeded",
            cancellationToken);
        var activeSkills = RuntimeInputGuard.CopyBounded(
            source.ActiveSkills
            ?? throw new ArgumentException(
                "A durable continuation requires an active-skill collection.",
                nameof(source)),
            DurableRunInputJournalCodec.MaxActiveSkills,
            skill => new SkillReference(
                (skill
                 ?? throw new ArgumentException(
                     "Active-skill collections cannot contain null entries.",
                     nameof(source))).SkillId,
                skill.Version),
            nameof(source.ActiveSkills),
            "activated_skill_count_exceeded",
            cancellationToken);
        DurableRunInputJournalCodec.ValidateUniqueActiveSkills(activeSkills);
        cancellationToken.ThrowIfCancellationRequested();
        return new DurableRunContinuation
        {
            Context = context,
            ActiveSkills = activeSkills,
            ReplaceActiveSkills = source.ReplaceActiveSkills,
            LaneId = source.LaneId,
            WorkloadClass = source.WorkloadClass is null
                ? null
                : ProviderWorkloadClasses.Normalize(
                    source.WorkloadClass,
                    nameof(source.WorkloadClass)),
            RequestCancellation = source.RequestCancellation,
            FinalOutputContract = source.FinalOutputContract?.Snapshot()
        };
    }

    public static DurableRunResumeGuard? Snapshot(
        DurableRunResumeGuard? source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var expectedBatchId = source.ExpectedBatchId is null
            ? null
            : RuntimeGuard.RequiredId(
                source.ExpectedBatchId,
                nameof(source.ExpectedBatchId));
        var expectedAgentId = source.ExpectedAgentId is null
            ? null
            : RuntimeGuard.RequiredId(
                source.ExpectedAgentId,
                nameof(source.ExpectedAgentId));
        var expectedDecisionKey = source.ExpectedDecisionKey is null
            ? null
            : MultiActorDecisionCoordinator.RequiredDecisionKey(
                source.ExpectedDecisionKey,
                nameof(source.ExpectedDecisionKey));
        var extensionName = source.RequiredInt32ExtensionName is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                source.RequiredInt32ExtensionName,
                128,
                nameof(source.RequiredInt32ExtensionName));
        var hasInt32Constraints = source.ExpectedInt32ExtensionValue.HasValue
                                  || source.MinimumInt32ExtensionValue
                                  != int.MinValue
                                  || source.MaximumInt32ExtensionValue
                                  != int.MaxValue;
        if (extensionName is null && hasInt32Constraints)
        {
            throw new ArgumentException(
                "An Int32 extension name is required when Int32 constraints are set.",
                nameof(source.RequiredInt32ExtensionName));
        }

        if (source.MinimumInt32ExtensionValue
            > source.MaximumInt32ExtensionValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source.MinimumInt32ExtensionValue));
        }

        if (source.ExpectedInt32ExtensionValue is int expected
            && (expected < source.MinimumInt32ExtensionValue
                || expected > source.MaximumInt32ExtensionValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(source.ExpectedInt32ExtensionValue));
        }

        var semanticName = source.SemanticExtensionName is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                source.SemanticExtensionName,
                128,
                nameof(source.SemanticExtensionName));
        var semanticDigest = source.ExpectedSemanticExtensionSha256;
        if ((semanticName is null) != (semanticDigest is null))
        {
            throw new ArgumentException(
                "A semantic extension name and expected SHA-256 digest must be supplied together.",
                nameof(source.SemanticExtensionName));
        }

        if (semanticDigest is not null
            && !CanonicalJsonDigest.IsSha256(semanticDigest))
        {
            throw new ArgumentException(
                "The expected semantic extension digest must be lowercase SHA-256.",
                nameof(source.ExpectedSemanticExtensionSha256));
        }

        return new DurableRunResumeGuard
        {
            ExpectedBatchId = expectedBatchId,
            ExpectedAgentId = expectedAgentId,
            ExpectedDecisionKey = expectedDecisionKey,
            RequiredInt32ExtensionName = extensionName,
            MinimumInt32ExtensionValue = source.MinimumInt32ExtensionValue,
            MaximumInt32ExtensionValue = source.MaximumInt32ExtensionValue,
            ExpectedInt32ExtensionValue = source.ExpectedInt32ExtensionValue,
            SemanticExtensionName = semanticName,
            ExpectedSemanticExtensionSha256 = semanticDigest
        };
    }

    public static MultiActorDecisionBatch Snapshot(
        MultiActorDecisionBatch source,
        int maximumRuns,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        if (maximumRuns is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRuns));
        }

        var runs = RuntimeInputGuard.CopyBounded(
            source.Runs,
            maximumRuns,
            request => Snapshot(
                request
                ?? throw new ArgumentException(
                    "Multi-actor batches cannot contain null runs.",
                    nameof(source)),
                cancellationToken),
            nameof(source.Runs),
            "multi_actor_batch_size_exceeded",
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new MultiActorDecisionBatch(
            source.BatchId,
            source.Coordinate,
            runs,
            source.AggregateBudget);
    }
}
