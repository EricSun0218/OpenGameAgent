using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameAgent.Protocol;

public sealed class ProtocolValidationError
{
    public ProtocolValidationError(string path, string code, string message)
    {
        Path = path;
        Code = code;
        Message = message;
    }

    public string Path { get; }

    public string Code { get; }

    public string Message { get; }
}

public static class ProtocolValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Regex IdPattern = new(
        "^[A-Za-z0-9._:-]+$",
        RegexOptions.CultureInvariant);

    private static readonly Regex NamePattern = new(
        "^[a-z][a-z0-9_.-]*$",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ObservationKindValues =
        new(StringComparer.Ordinal)
        {
            ObservationKinds.Event,
            ObservationKinds.Snapshot,
            ObservationKinds.Patch,
            ObservationKinds.Document,
            ObservationKinds.Metric,
            ObservationKinds.Relation,
            ObservationKinds.ResourceReference,
            ObservationKinds.Custom
        };

    private static readonly HashSet<string> ObservationTrustValues =
        new(StringComparer.Ordinal)
        {
            ObservationTrustLevels.Authoritative,
            ObservationTrustLevels.Trusted,
            ObservationTrustLevels.Untrusted
        };

    private static readonly HashSet<string> ObservationVisibilityValues =
        new(StringComparer.Ordinal)
        {
            ObservationVisibilityScopes.World,
            ObservationVisibilityScopes.Group,
            ObservationVisibilityScopes.Agent,
            ObservationVisibilityScopes.Private
        };

    private static readonly HashSet<string> ToolEffectValues = new(StringComparer.Ordinal)
    {
        ToolEffects.PureRead,
        ToolEffects.AgentLocalWrite,
        ToolEffects.WorldCommand,
        ToolEffects.ExternalWrite
    };

    private static readonly HashSet<string> ThreadAffinityValues = new(StringComparer.Ordinal)
    {
        ThreadAffinities.AnyThread,
        ThreadAffinities.EngineMainThread,
        ThreadAffinities.HostManaged
    };

    private static readonly HashSet<string> ToolRetryPolicyValues =
        new(StringComparer.Ordinal)
        {
            ToolRetryPolicies.Never,
            ToolRetryPolicies.SafeRead,
            ToolRetryPolicies.Idempotent
        };

    private static readonly HashSet<string> ToolIdempotencyPolicyValues =
        new(StringComparer.Ordinal)
        {
            ToolIdempotencyPolicies.Required,
            ToolIdempotencyPolicies.BestEffort,
            ToolIdempotencyPolicies.None
        };

    private static readonly HashSet<string> ToolVisibilityValues =
        new(StringComparer.Ordinal)
        {
            ToolVisibilities.Direct,
            ToolVisibilities.Deferred,
            ToolVisibilities.Internal
        };

    private static readonly HashSet<string> ReceiptStatusValues = new(StringComparer.Ordinal)
    {
        ReceiptStatuses.Succeeded,
        ReceiptStatuses.Rejected,
        ReceiptStatuses.Failed,
        ReceiptStatuses.Unknown
    };

    private static readonly HashSet<string> RunStateValues = new(StringComparer.Ordinal)
    {
        RunStates.Queued,
        RunStates.Preparing,
        RunStates.Running,
        RunStates.Cancelling,
        RunStates.Interrupting,
        RunStates.WaitingForAction,
        RunStates.Reconciling,
        RunStates.Completed,
        RunStates.BudgetExhausted,
        RunStates.Interrupted,
        RunStates.Cancelled,
        RunStates.Failed
    };

    private static readonly HashSet<string> CompletionIntentValues = new(StringComparer.Ordinal)
    {
        CompletionIntents.Cancelled,
        CompletionIntents.Interrupted,
        CompletionIntents.Failed
    };

    private static readonly HashSet<string> SkillTrustValues =
        new(StringComparer.Ordinal)
        {
            "builtin",
            "trusted",
            "untrusted"
        };

    private static readonly HashSet<string> EngineValues =
        new(StringComparer.Ordinal)
        {
            "headless",
            "godot",
            "unity",
            "unreal"
        };

    private static readonly HashSet<string> PersistenceLevelValues =
        new(StringComparer.Ordinal)
        {
            "none",
            "memory",
            "durable"
        };

    private static readonly HashSet<string> TriggerTypeValues = new(StringComparer.Ordinal)
    {
        "interaction",
        "world_event",
        "scheduled",
        "manual",
        "generation"
    };

    public static IReadOnlyList<ProtocolValidationError> Validate(ObservationEnvelope value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.ObservationId, "$.observationId", errors);
        RequiredId(value.WorldId, "$.worldId", errors);
        OptionalId(value.SessionId, "$.sessionId", errors);
        Required(value.Source, "$.source", errors);
        Required(value.Kind, "$.kind", errors);
        Required(value.ContentType, "$.contentType", errors);
        Required(value.Trust, "$.trust", errors);
        OptionalBounded(
            value.Source,
            128,
            "$.source",
            requireNonEmpty: false,
            errors);
        OptionalBounded(
            value.ContentType,
            128,
            "$.contentType",
            requireNonEmpty: false,
            errors);
        OptionalBounded(
            value.ContentSchemaVersion,
            32,
            "$.contentSchemaVersion",
            requireNonEmpty: true,
            errors);
        OptionalBounded(
            value.StateVersion,
            128,
            "$.stateVersion",
            requireNonEmpty: true,
            errors);
        OptionalBounded(
            value.CacheKey,
            256,
            "$.cacheKey",
            requireNonEmpty: true,
            errors);
        ValidateBoundedIds(
            value.SubjectIds,
            ProtocolLimits.MaxObservationSubjectIds,
            "$.subjectIds",
            errors,
            requireUnique: true);
        if (!ObservationKindValues.Contains(value.Kind))
        {
            errors.Add(new(
                "$.kind",
                "unknown_value",
                "Unknown observation kind."));
        }

        if (!ObservationTrustValues.Contains(value.Trust))
        {
            errors.Add(new(
                "$.trust",
                "unknown_value",
                "Unknown observation trust level."));
        }

        if (value.Visibility is null)
        {
            errors.Add(new(
                "$.visibility",
                "invalid_type",
                "visibility must be an object."));
        }
        else
        {
            Required(value.Visibility.Scope, "$.visibility.scope", errors);
            if (!ObservationVisibilityValues.Contains(
                    value.Visibility.Scope))
            {
                errors.Add(new(
                    "$.visibility.scope",
                    "unknown_value",
                    "Unknown observation visibility scope."));
            }

            ValidateBoundedIds(
                value.Visibility.AudienceIds,
                ProtocolLimits.MaxObservationAudienceIds,
                "$.visibility.audienceIds",
                errors,
                requireUnique: true);
        }

        ValidateExtensions(value.Extensions, errors);

        if (value.Payload.HasValue == (value.ResourceRef is not null))
        {
            errors.Add(new(
                "$",
                "exactly_one_content",
                "Exactly one of payload or resourceRef is required."));
        }
        else if (value.Payload.HasValue)
        {
            ValidateJsonValue(value.Payload.Value, "$.payload", errors);
        }

        if (value.ResourceRef is not null)
        {
            ValidateResourceReference(
                value.ResourceRef,
                "$.resourceRef",
                errors);
        }

        if (value.TtlMs is < 0)
        {
            errors.Add(new(
                "$.ttlMs",
                "out_of_range",
                "ttlMs must not be negative."));
        }

        if (value.Sequence is < 0)
        {
            errors.Add(new(
                "$.sequence",
                "out_of_range",
                "sequence must not be negative."));
        }

        if (value.Priority is < -1_000 or > 1_000)
        {
            errors.Add(new(
                "$.priority",
                "out_of_range",
                "priority must be between -1000 and 1000."));
        }

        if (string.Equals(value.Kind, "patch", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(value.StateVersion))
        {
            errors.Add(new(
                "$.stateVersion",
                "patch_requires_state_version",
                "Patch observations require stateVersion."));
        }

        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(ToolDescriptor value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredName(value.Name, "$.name", errors);
        Required(value.Version, "$.version", errors);
        Required(value.Description, "$.description", errors);
        Required(value.Toolset, "$.toolset", errors);
        OptionalBounded(
            value.Version,
            32,
            "$.version",
            requireNonEmpty: false,
            errors);
        OptionalBounded(
            value.Description,
            2_048,
            "$.description",
            requireNonEmpty: false,
            errors);
        OptionalBounded(
            value.Toolset,
            96,
            "$.toolset",
            requireNonEmpty: true,
            errors);

        if (value.ParametersSchema.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new(
                "$.parametersSchema",
                "invalid_type",
                "parametersSchema must be an object."));
        }
        else
        {
            ValidateJsonValue(
                value.ParametersSchema,
                "$.parametersSchema",
                errors);
        }

        if (value.ResultSchema.HasValue
            && value.ResultSchema.Value.ValueKind
            != JsonValueKind.Object)
        {
            errors.Add(new(
                "$.resultSchema",
                "invalid_type",
                "resultSchema must be an object when present."));
        }
        else if (value.ResultSchema.HasValue)
        {
            ValidateJsonValue(
                value.ResultSchema.Value,
                "$.resultSchema",
                errors);
        }

        ValidateBoundedStrings(
            value.ConflictScopes,
            ProtocolLimits.MaxToolConflictScopes,
            128,
            "$.conflictScopes",
            requireNonEmpty: true,
            errors,
            requireUnique: true);

        if (!ToolEffectValues.Contains(value.Effect))
        {
            errors.Add(new("$.effect", "unknown_value", "Unknown tool effect."));
        }

        if (!ThreadAffinityValues.Contains(value.ThreadAffinity))
        {
            errors.Add(new("$.threadAffinity", "unknown_value", "Unknown thread affinity."));
        }

        if (!ToolRetryPolicyValues.Contains(value.RetryPolicy))
        {
            errors.Add(new(
                "$.retryPolicy",
                "unknown_value",
                "Unknown tool retry policy."));
        }

        if (!ToolIdempotencyPolicyValues.Contains(value.IdempotencyPolicy))
        {
            errors.Add(new(
                "$.idempotencyPolicy",
                "unknown_value",
                "Unknown tool idempotency policy."));
        }

        if (!ToolVisibilityValues.Contains(value.Visibility))
        {
            errors.Add(new(
                "$.visibility",
                "unknown_value",
                "Unknown tool visibility."));
        }

        if ((value.Effect == ToolEffects.WorldCommand || value.Effect == ToolEffects.ExternalWrite)
            && string.Equals(
                value.IdempotencyPolicy,
                ToolIdempotencyPolicies.None,
                StringComparison.Ordinal))
        {
            errors.Add(new(
                "$.idempotencyPolicy",
                "side_effect_requires_idempotency",
                "Side-effecting tools cannot use idempotencyPolicy 'none'."));
        }

        if (value.TimeoutMs is <= 0 or > 86_400_000)
        {
            errors.Add(new(
                "$.timeoutMs",
                "out_of_range",
                "timeoutMs must be between 1 and 86400000."));
        }

        ValidateExtensions(value.Extensions, errors);

        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(
        ToolInvocation value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.ToolCallId, "$.toolCallId", errors);
        RequiredId(value.RunId, "$.runId", errors);
        RequiredId(value.TurnId, "$.turnId", errors);
        RequiredId(value.AttemptId, "$.attemptId", errors);
        RequiredName(value.ToolName, "$.toolName", errors);
        Required(value.ToolVersion, "$.toolVersion", errors);
        OptionalBounded(
            value.ToolVersion,
            32,
            "$.toolVersion",
            requireNonEmpty: false,
            errors);
        if (value.Arguments.ValueKind == JsonValueKind.Undefined)
        {
            errors.Add(new(
                "$.arguments",
                "required",
                "arguments is required."));
        }
        else
        {
            ValidateJsonValue(value.Arguments, "$.arguments", errors);
        }

        if (!ToolEffectValues.Contains(value.Effect))
        {
            errors.Add(new(
                "$.effect",
                "unknown_value",
                "Unknown tool effect."));
        }

        ValidateBoundedStrings(
            value.ResolvedConflictKeys,
            ProtocolLimits.MaxToolResolvedConflictKeys,
            ProtocolLimits.MaxToolResolvedConflictKeyUnicodeScalars,
            "$.resolvedConflictKeys",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        if (value.Sequence < 0)
        {
            errors.Add(new(
                "$.sequence",
                "out_of_range",
                "sequence must not be negative."));
        }

        ValidateExtensions(value.Extensions, errors);

        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(ActionRequest value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.OperationId, "$.operationId", errors);
        RequiredId(value.RunId, "$.runId", errors);
        RequiredId(value.TurnId, "$.turnId", errors);
        RequiredId(value.ToolCallId, "$.toolCallId", errors);
        RequiredId(value.AgentId, "$.agentId", errors);
        RequiredId(value.WorldId, "$.worldId", errors);
        RequiredName(value.ActionName, "$.actionName", errors);
        Required(value.ActionVersion, "$.actionVersion", errors);
        OptionalBounded(
            value.ActionVersion,
            32,
            "$.actionVersion",
            requireNonEmpty: false,
            errors);
        if (value.Arguments.ValueKind == JsonValueKind.Undefined)
        {
            errors.Add(new(
                "$.arguments",
                "required",
                "arguments is required."));
        }
        else
        {
            ValidateJsonValue(value.Arguments, "$.arguments", errors);
        }

        OptionalBounded(
            value.BasedOnStateVersion,
            128,
            "$.basedOnStateVersion",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.DecisionKey,
            256,
            "$.decisionKey",
            requireNonEmpty: true,
            errors);
        OptionalId(value.BatchId, "$.batchId", errors);
        ValidateBoundedStrings(
            value.ExpectedEffects,
            ProtocolLimits.MaxActionExpectedEffects,
            ProtocolLimits.MaxActionExpectedEffectUnicodeScalars,
            "$.expectedEffects",
            requireNonEmpty: false,
            errors);
        OptionalBounded(
            value.ReasonCode,
            128,
            "$.reasonCode",
            requireNonEmpty: true,
            errors);
        ValidateExtensions(value.Extensions, errors);

        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(ActionReceipt value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.OperationId, "$.operationId", errors);

        if (value.Revision < 0)
        {
            errors.Add(new("$.revision", "out_of_range", "revision must not be negative."));
        }

        if (!ReceiptStatusValues.Contains(value.Status))
        {
            errors.Add(new("$.status", "unknown_value", "Unknown receipt status."));
        }

        OptionalBounded(
            value.ErrorCode,
            128,
            "$.errorCode",
            requireNonEmpty: true,
            errors);
        if (value.Result.HasValue)
        {
            ValidateJsonValue(value.Result.Value, "$.result", errors);
        }
        if (value.StateDiff.HasValue)
        {
            ValidateJsonValue(value.StateDiff.Value, "$.stateDiff", errors);
        }

        if (value.AuthoritativeObservations is null)
        {
            errors.Add(new(
                "$.authoritativeObservations",
                "invalid_type",
                "authoritativeObservations must be an array when present."));
        }
        else
        {
            if (value.AuthoritativeObservations.Count
                > ProtocolLimits.MaxAuthoritativeObservationsPerReceipt)
            {
                errors.Add(new(
                    "$.authoritativeObservations",
                    "out_of_range",
                    "authoritativeObservations cannot exceed "
                    + ProtocolLimits.MaxAuthoritativeObservationsPerReceipt
                    + " items."));
            }

            for (var index = 0;
                 index < Math.Min(
                     value.AuthoritativeObservations.Count,
                     ProtocolLimits.MaxAuthoritativeObservationsPerReceipt);
                 index++)
            {
                var observation =
                    value.AuthoritativeObservations[index];
                var path =
                    "$.authoritativeObservations[" + index + "]";
                if (observation is null)
                {
                    errors.Add(new(
                        path,
                        "invalid_type",
                        "An authoritative observation must be an object."));
                    continue;
                }

                foreach (var nested in Validate(observation))
                {
                    errors.Add(new(
                        nested.Path == "$"
                            ? path
                            : path + nested.Path[1..],
                        nested.Code,
                        nested.Message));
                }
            }
        }

        ValidateExtensions(value.Extensions, errors);

        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(
        TurnSnapshot value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.TurnId, "$.turnId", errors);
        RequiredId(value.RunId, "$.runId", errors);
        if (value.RuntimeGeneration < 1)
        {
            errors.Add(new(
                "$.runtimeGeneration",
                "out_of_range",
                "runtimeGeneration must be positive."));
        }

        Required(value.ProviderId, "$.providerId", errors);
        Required(value.ModelId, "$.modelId", errors);
        Required(
            value.PromptLayoutVersion,
            "$.promptLayoutVersion",
            errors);
        Required(value.StablePrefixHash, "$.stablePrefixHash", errors);
        OptionalUnicodeScalars(
            value.ProviderId,
            ProtocolLimits.MaxProviderIdUnicodeScalars,
            "$.providerId",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.ModelId,
            ProtocolLimits.MaxModelIdUnicodeScalars,
            "$.modelId",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.PromptLayoutVersion,
            ProtocolLimits.MaxTurnPolicyVersionUnicodeScalars,
            "$.promptLayoutVersion",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.StablePrefixHash,
            256,
            "$.stablePrefixHash",
            requireNonEmpty: true,
            errors);
        if (value.SkillGeneration < 0)
        {
            errors.Add(new(
                "$.skillGeneration",
                "out_of_range",
                "skillGeneration must not be negative."));
        }

        ValidateBoundedStrings(
            value.SkillDigests,
            int.MaxValue,
            256,
            "$.skillDigests",
            requireNonEmpty: true,
            errors);
        if (value.ToolCatalogGeneration < 0)
        {
            errors.Add(new(
                "$.toolCatalogGeneration",
                "out_of_range",
                "toolCatalogGeneration must not be negative."));
        }

        Required(value.DirectToolDigest, "$.directToolDigest", errors);
        Required(
            value.ContextPolicyVersion,
            "$.contextPolicyVersion",
            errors);
        Required(
            value.BudgetPolicyVersion,
            "$.budgetPolicyVersion",
            errors);
        OptionalUnicodeScalars(
            value.DirectToolDigest,
            256,
            "$.directToolDigest",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.DeferredCatalogDigest,
            256,
            "$.deferredCatalogDigest",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.ContextPolicyVersion,
            ProtocolLimits.MaxTurnPolicyVersionUnicodeScalars,
            "$.contextPolicyVersion",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.BudgetPolicyVersion,
            ProtocolLimits.MaxTurnPolicyVersionUnicodeScalars,
            "$.budgetPolicyVersion",
            requireNonEmpty: true,
            errors);
        if (value.MaxSideEffectToolCallsPerTurn is < 0 or > 4_096)
        {
            errors.Add(new(
                "$.maxSideEffectToolCallsPerTurn",
                "out_of_range",
                "maxSideEffectToolCallsPerTurn must be between 0 and 4096."));
        }

        ValidateExtensions(value.Extensions, errors);

        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(
        RuntimeEvent value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.EventId, "$.eventId", errors);
        OptionalId(value.RunId, "$.runId", errors);
        OptionalId(value.TurnId, "$.turnId", errors);
        OptionalId(value.AttemptId, "$.attemptId", errors);
        OptionalId(value.StreamAttemptId, "$.streamAttemptId", errors);
        if (value.Sequence < 0)
        {
            errors.Add(new(
                "$.sequence",
                "out_of_range",
                "sequence must not be negative."));
        }

        Required(value.Kind, "$.kind", errors);
        OptionalUnicodeScalars(
            value.Kind,
            96,
            "$.kind",
            requireNonEmpty: true,
            errors);
        if (value.Durability is not EventDurabilities.Durable
            and not EventDurabilities.Ephemeral)
        {
            errors.Add(new(
                "$.durability",
                "unknown_value",
                "Unknown event durability."));
        }

        if (value.RuntimeGeneration < 1)
        {
            errors.Add(new(
                "$.runtimeGeneration",
                "out_of_range",
                "runtimeGeneration must be positive."));
        }

        OptionalUnicodeScalars(
            value.ProviderId,
            ProtocolLimits.MaxProviderIdUnicodeScalars,
            "$.providerId",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.ModelId,
            ProtocolLimits.MaxModelIdUnicodeScalars,
            "$.modelId",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.TransportDialect,
            128,
            "$.transportDialect",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.ProviderCapabilityDigest,
            256,
            "$.providerCapabilityDigest",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.ProviderRouteDigest,
            256,
            "$.providerRouteDigest",
            requireNonEmpty: true,
            errors);
        OptionalUnicodeScalars(
            value.ReasonCode,
            ProtocolLimits.MaxRuntimeEventReasonCodeUnicodeScalars,
            "$.reasonCode",
            requireNonEmpty: true,
            errors);
        if (value.Payload.ValueKind == JsonValueKind.Undefined)
        {
            errors.Add(new(
                "$.payload",
                "required",
                "payload is required."));
        }
        else
        {
            ValidateJsonValue(value.Payload, "$.payload", errors);
        }

        ValidateExtensions(value.Extensions, errors);

        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(
        AgentDefinition value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(
            value.AgentDefinitionId,
            "$.agentDefinitionId",
            errors);
        ValidateRequiredBoundedString(
            value.Version,
            32,
            "$.version",
            errors);
        ValidateJsonValue(value.Identity, "$.identity", errors);
        OptionalUnicodeScalars(
            value.BehaviorPolicyRef,
            256,
            "$.behaviorPolicyRef",
            requireNonEmpty: true,
            errors);
        ValidateBoundedStrings(
            value.Toolsets,
            ProtocolLimits.MaxAgentDefinitionReferences,
            96,
            "$.toolsets",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        ValidateBoundedIds(
            value.Skills,
            ProtocolLimits.MaxAgentDefinitionReferences,
            "$.skills",
            errors,
            requireUnique: true);
        OptionalUnicodeScalars(
            value.ContextPolicyRef,
            256,
            "$.contextPolicyRef",
            requireNonEmpty: false,
            errors);
        OptionalUnicodeScalars(
            value.MemoryPolicyRef,
            256,
            "$.memoryPolicyRef",
            requireNonEmpty: false,
            errors);
        OptionalUnicodeScalars(
            value.ProviderPolicyRef,
            256,
            "$.providerPolicyRef",
            requireNonEmpty: false,
            errors);
        ValidateJsonObject(value.Budgets, "$.budgets", errors);
        ValidateExtensions(value.Extensions, errors);
        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(
        SkillManifest value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.SkillId, "$.skillId", errors);
        ValidateRequiredBoundedString(
            value.Version,
            32,
            "$.version",
            errors);
        ValidateRequiredBoundedString(
            value.Digest,
            256,
            "$.digest",
            errors);
        ValidateRequiredBoundedString(
            value.Description,
            2_048,
            "$.description",
            errors);
        ValidateBoundedStrings(
            value.PromptFragments,
            ProtocolLimits.MaxSkillManifestListItems,
            ProtocolLimits.MaxSkillPromptFragmentUnicodeScalars,
            "$.promptFragments",
            requireNonEmpty: true,
            errors,
            requireUnique: false);
        ValidateBoundedStrings(
            value.RequiredToolRefs,
            ProtocolLimits.MaxSkillManifestListItems,
            160,
            "$.requiredToolRefs",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        ValidateBoundedStrings(
            value.OptionalToolRefs,
            ProtocolLimits.MaxSkillManifestListItems,
            160,
            "$.optionalToolRefs",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        ValidateBoundedStrings(
            value.ContextProviderRefs,
            ProtocolLimits.MaxSkillManifestListItems,
            160,
            "$.contextProviderRefs",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        ValidateResourceReferences(
            value.ResourceRefs,
            ProtocolLimits.MaxSkillManifestListItems,
            "$.resourceRefs",
            errors,
            requireUniqueUris: true);
        ValidateJsonObject(
            value.CapabilityRequirements,
            "$.capabilityRequirements",
            errors);
        Required(value.Trust, "$.trust", errors);
        if (!SkillTrustValues.Contains(value.Trust))
        {
            errors.Add(new(
                "$.trust",
                "unknown_value",
                "Unknown skill trust level."));
        }

        ValidateJsonObject(
            value.ActivationPolicy,
            "$.activationPolicy",
            errors);
        ValidateExtensions(value.Extensions, errors);
        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(
        CapabilityManifest value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        ValidateRequiredBoundedString(
            value.ProtocolRange,
            64,
            "$.protocolRange",
            errors);
        ValidateRequiredBoundedString(
            value.RuntimeVersion,
            32,
            "$.runtimeVersion",
            errors);
        Required(value.Engine, "$.engine", errors);
        if (!EngineValues.Contains(value.Engine))
        {
            errors.Add(new(
                "$.engine",
                "unknown_value",
                "Unknown engine."));
        }

        ValidateRequiredBoundedString(
            value.EngineVersion,
            64,
            "$.engineVersion",
            errors);
        ValidateRequiredBoundedString(
            value.AdapterVersion,
            32,
            "$.adapterVersion",
            errors);
        ValidateRequiredBoundedString(
            value.Platform,
            64,
            "$.platform",
            errors);
        ValidateRequiredBoundedString(
            value.Backend,
            64,
            "$.backend",
            errors);
        ValidateBoundedStrings(
            value.ContentTypes,
            ProtocolLimits.MaxCapabilityManifestListItems,
            128,
            "$.contentTypes",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        ValidateBoundedStrings(
            value.Codecs,
            ProtocolLimits.MaxCapabilityManifestListItems,
            64,
            "$.codecs",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        ValidateBoundedStrings(
            value.Transports,
            ProtocolLimits.MaxCapabilityManifestListItems,
            64,
            "$.transports",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        if (value.MaxMessageBytes < 1)
        {
            errors.Add(new(
                "$.maxMessageBytes",
                "out_of_range",
                "maxMessageBytes must be positive."));
        }

        if (value.MaxBatchSize < 1)
        {
            errors.Add(new(
                "$.maxBatchSize",
                "out_of_range",
                "maxBatchSize must be positive."));
        }

        Required(
            value.PersistenceLevel,
            "$.persistenceLevel",
            errors);
        if (!PersistenceLevelValues.Contains(value.PersistenceLevel))
        {
            errors.Add(new(
                "$.persistenceLevel",
                "unknown_value",
                "Unknown persistence level."));
        }

        ValidateBoundedEnumValues(
            value.ToolEffects,
            ProtocolLimits.MaxCapabilityManifestListItems,
            ToolEffectValues,
            "$.toolEffects",
            errors);
        ValidateBoundedEnumValues(
            value.ThreadAffinities,
            ProtocolLimits.MaxCapabilityManifestListItems,
            ThreadAffinityValues,
            "$.threadAffinities",
            errors);
        ValidateJsonObject(
            value.ProviderCapabilities,
            "$.providerCapabilities",
            errors);
        ValidateExtensions(value.Extensions, errors);
        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(
        ContextBudgetReport value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.RunId, "$.runId", errors);
        RequiredId(value.TurnId, "$.turnId", errors);
        if (value.InputCount < 0)
        {
            errors.Add(new(
                "$.inputCount",
                "out_of_range",
                "inputCount must not be negative."));
        }

        ValidateBoundedIds(
            value.SelectedIds,
            ProtocolLimits.MaxContextBudgetReportItems,
            "$.selectedIds",
            errors,
            requireUnique: true);
        ValidateBoundedIds(
            value.DeferredIds,
            ProtocolLimits.MaxContextBudgetReportItems,
            "$.deferredIds",
            errors,
            requireUnique: true);
        ValidatePrunedContextItems(value.Pruned, errors);
        ValidateResourceReferences(
            value.Externalized,
            ProtocolLimits.MaxContextBudgetReportItems,
            "$.externalized",
            errors,
            requireUniqueUris: true);
        if (value.EstimatedTokens < 0)
        {
            errors.Add(new(
                "$.estimatedTokens",
                "out_of_range",
                "estimatedTokens must not be negative."));
        }

        if (value.ActualTokens is < 0)
        {
            errors.Add(new(
                "$.actualTokens",
                "out_of_range",
                "actualTokens must not be negative."));
        }

        if (value.BudgetLimit < 1)
        {
            errors.Add(new(
                "$.budgetLimit",
                "out_of_range",
                "budgetLimit must be positive."));
        }

        ValidateBoundedStrings(
            value.ReasonCodes,
            ProtocolLimits.MaxContextBudgetReportItems,
            128,
            "$.reasonCodes",
            requireNonEmpty: true,
            errors,
            requireUnique: true);
        ValidateExtensions(value.Extensions, errors);
        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(AgentRun value)
    {
        var errors = new List<ProtocolValidationError>();
        ValidateVersion(value, errors);
        RequiredId(value.RunId, "$.runId", errors);
        RequiredId(value.AgentId, "$.agentId", errors);
        RequiredId(value.WorldId, "$.worldId", errors);
        Required(value.State, "$.state", errors);
        OptionalId(value.SessionId, "$.sessionId", errors);
        OptionalId(value.BatchId, "$.batchId", errors);
        OptionalId(value.CurrentTurnId, "$.currentTurnId", errors);
        OptionalUnicodeScalars(
            value.DecisionKey,
            256,
            "$.decisionKey",
            requireNonEmpty: true,
            errors);
        OptionalBounded(
            value.TerminalReason,
            128,
            "$.terminalReason",
            requireNonEmpty: false,
            errors);

        if (!RunStateValues.Contains(value.State))
        {
            errors.Add(new("$.state", "unknown_value", "Unknown run state."));
        }

        if (value.Trigger is null)
        {
            errors.Add(new(
                "$.trigger",
                "required",
                "A trigger is required."));
        }
        else
        {
            Required(value.Trigger.Type, "$.trigger.type", errors);
            if (!TriggerTypeValues.Contains(value.Trigger.Type))
            {
                errors.Add(new(
                    "$.trigger.type",
                    "unknown_value",
                    "Unknown trigger type."));
            }

            OptionalId(
                value.Trigger.SourceId,
                "$.trigger.sourceId",
                errors);
        }

        ValidateUniqueIds(
            value.TriggerObservationIds,
            "$.triggerObservationIds",
            errors);
        ValidateUniqueIds(
            value.PendingOperationIds,
            "$.pendingOperationIds",
            errors);

        if (value.CompletionIntent is not null
            && !CompletionIntentValues.Contains(value.CompletionIntent))
        {
            errors.Add(new(
                "$.completionIntent",
                "unknown_value",
                "Unknown completion intent."));
        }

        if (value.Revision < 0)
        {
            errors.Add(new(
                "$.revision",
                "out_of_range",
                "revision must not be negative."));
        }

        if (value.RuntimeGeneration < 1)
        {
            errors.Add(new(
                "$.runtimeGeneration",
                "out_of_range",
                "runtimeGeneration must be at least one."));
        }

        if (value.Budget is null)
        {
            errors.Add(new(
                "$.budget",
                "required",
                "A budget is required."));
        }
        else
        {
            Validate(value.Budget, "$.budget", errors);
        }

        if (value.Usage is null)
        {
            errors.Add(new(
                "$.usage",
                "required",
                "Usage is required."));
        }
        else
        {
            Validate(value.Usage, "$.usage", errors);
        }

        ValidateExtensions(value.Extensions, errors);

        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(AgentBudget value)
    {
        var errors = new List<ProtocolValidationError>();
        Validate(value, "$", errors);
        return errors;
    }

    public static IReadOnlyList<ProtocolValidationError> Validate(AgentUsage value)
    {
        var errors = new List<ProtocolValidationError>();
        Validate(value, "$", errors);
        return errors;
    }

    public static void EnsureValid(ObservationEnvelope value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(ToolDescriptor value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(ToolInvocation value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(ActionRequest value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(ActionReceipt value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(TurnSnapshot value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(RuntimeEvent value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(AgentDefinition value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(SkillManifest value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(CapabilityManifest value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(ContextBudgetReport value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(AgentRun value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(AgentBudget value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(AgentUsage value)
    {
        ThrowIfInvalid(Validate(value));
    }

    private static void ValidateRequiredBoundedString(
        string? value,
        int maximumLength,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        Required(value, path, errors);
        OptionalUnicodeScalars(
            value,
            maximumLength,
            path,
            requireNonEmpty: false,
            errors);
    }

    private static void ValidateBoundedIds(
        IReadOnlyList<string>? values,
        int maximumItems,
        string path,
        ICollection<ProtocolValidationError> errors,
        bool requireUnique)
    {
        if (values is null)
        {
            errors.Add(new(
                path,
                "invalid_type",
                "An identifier collection is required."));
            return;
        }

        if (values.Count > maximumItems)
        {
            errors.Add(new(
                path,
                "out_of_range",
                $"The collection cannot exceed {maximumItems} items."));
        }

        var seen = requireUnique
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        for (var index = 0; index < values.Count; index++)
        {
            var itemPath = path + "[" + index + "]";
            var item = values[index];
            RequiredId(item, itemPath, errors);
            if (seen is not null && !seen.Add(item))
            {
                errors.Add(new(
                    itemPath,
                    "duplicate_value",
                    "The collection must contain unique values."));
            }
        }
    }

    private static void ValidateBoundedEnumValues(
        IReadOnlyList<string>? values,
        int maximumItems,
        ISet<string> allowedValues,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (values is null)
        {
            errors.Add(new(
                path,
                "invalid_type",
                "A collection is required."));
            return;
        }

        if (values.Count > maximumItems)
        {
            errors.Add(new(
                path,
                "out_of_range",
                $"The collection cannot exceed {maximumItems} items."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var item = values[index];
            var itemPath = path + "[" + index + "]";
            Required(item, itemPath, errors);
            if (!allowedValues.Contains(item))
            {
                errors.Add(new(
                    itemPath,
                    "unknown_value",
                    "Unknown collection value."));
            }

            if (!seen.Add(item))
            {
                errors.Add(new(
                    itemPath,
                    "duplicate_value",
                    "The collection must contain unique values."));
            }
        }
    }

    private static void ValidateResourceReferences(
        IReadOnlyList<ResourceReference>? values,
        int maximumItems,
        string path,
        ICollection<ProtocolValidationError> errors,
        bool requireUniqueUris)
    {
        if (values is null)
        {
            errors.Add(new(
                path,
                "invalid_type",
                "A resource-reference collection is required."));
            return;
        }

        if (values.Count > maximumItems)
        {
            errors.Add(new(
                path,
                "out_of_range",
                $"The collection cannot exceed {maximumItems} items."));
        }

        var uris = requireUniqueUris
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        var count = Math.Min(values.Count, maximumItems);
        for (var index = 0; index < count; index++)
        {
            var itemPath = path + "[" + index + "]";
            var item = values[index];
            if (item is null)
            {
                errors.Add(new(
                    itemPath,
                    "invalid_type",
                    "A resource reference is required."));
                continue;
            }

            ValidateResourceReference(item, itemPath, errors);
            if (uris is not null
                && item.Uri is not null
                && !uris.Add(item.Uri))
            {
                errors.Add(new(
                    itemPath + ".uri",
                    "duplicate_value",
                    "Resource URIs must be unique."));
            }
        }
    }

    private static void ValidateResourceReference(
        ResourceReference value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        ValidateRequiredBoundedString(
            value.Uri,
            ProtocolLimits.MaxResourceUriUnicodeScalars,
            path + ".uri",
            errors);
        if (!string.IsNullOrWhiteSpace(value.Uri)
            && (!Uri.TryCreate(
                    value.Uri,
                    UriKind.RelativeOrAbsolute,
                    out _)
                || !Uri.IsWellFormedUriString(
                    value.Uri,
                    UriKind.RelativeOrAbsolute)))
        {
            errors.Add(new(
                path + ".uri",
                "invalid_uri",
                "The value must be a URI reference."));
        }

        ValidateRequiredBoundedString(
            value.MediaType,
            128,
            path + ".mediaType",
            errors);
        if (value.Digest is not null)
        {
            ValidateRequiredBoundedString(
                value.Digest,
                256,
                path + ".digest",
                errors);
        }

        if (value.SizeBytes is < 0)
        {
            errors.Add(new(
                path + ".sizeBytes",
                "out_of_range",
                "sizeBytes must not be negative."));
        }
    }

    private static void ValidatePrunedContextItems(
        IReadOnlyList<PrunedContextItem>? values,
        ICollection<ProtocolValidationError> errors)
    {
        const string path = "$.pruned";
        if (values is null)
        {
            errors.Add(new(
                path,
                "invalid_type",
                "A pruned-context collection is required."));
            return;
        }

        if (values.Count > ProtocolLimits.MaxContextBudgetReportItems)
        {
            errors.Add(new(
                path,
                "out_of_range",
                "The collection cannot exceed "
                + $"{ProtocolLimits.MaxContextBudgetReportItems} items."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = Math.Min(
            values.Count,
            ProtocolLimits.MaxContextBudgetReportItems);
        for (var index = 0; index < count; index++)
        {
            var itemPath = path + "[" + index + "]";
            var item = values[index];
            if (item is null)
            {
                errors.Add(new(
                    itemPath,
                    "invalid_type",
                    "A pruned-context item is required."));
                continue;
            }

            RequiredId(item.Id, itemPath + ".id", errors);
            ValidateRequiredBoundedString(
                item.Category,
                64,
                itemPath + ".category",
                errors);
            ValidateRequiredBoundedString(
                item.ReasonCode,
                128,
                itemPath + ".reasonCode",
                errors);
            if (!ids.Add(item.Id))
            {
                errors.Add(new(
                    itemPath + ".id",
                    "duplicate_value",
                    "Pruned context identifiers must be unique."));
            }
        }
    }

    private static void ValidateExtensions(
        IReadOnlyDictionary<string, JsonElement>? extensions,
        ICollection<ProtocolValidationError> errors)
    {
        const string path = "$.extensions";
        if (extensions is null)
        {
            errors.Add(new(
                path,
                "invalid_type",
                "extensions must be an object when present."));
            return;
        }

        if (extensions.Count > ProtocolLimits.MaxProtocolExtensions)
        {
            errors.Add(new(
                path,
                "out_of_range",
                "extensions cannot exceed "
                + $"{ProtocolLimits.MaxProtocolExtensions} properties."));
        }

        var count = 0;
        foreach (var extension in extensions)
        {
            if (count >= ProtocolLimits.MaxProtocolExtensions)
            {
                break;
            }

            var itemPath = path + "." + extension.Key;
            ValidateRequiredBoundedString(
                extension.Key,
                ProtocolLimits.MaxProtocolExtensionKeyUnicodeScalars,
                itemPath,
                errors);
            ValidateJsonValue(extension.Value, itemPath, errors);
            count++;
        }
    }

    private static void ValidateJsonObject(
        JsonElement value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new(
                path,
                "invalid_type",
                "A JSON object is required."));
        }

        ValidateJsonValue(value, path, errors);
    }

    private static void ValidateJsonValue(
        JsonElement value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            errors.Add(new(
                path,
                "required",
                "A JSON value is required."));
            return;
        }

        try
        {
            using var buffer = new ProtocolCountingBufferWriter(
                ProtocolLimits.MaxProtocolJsonUtf8Bytes);
            using var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions
                {
                    MaxDepth = ProtocolLimits.MaxProtocolJsonDepth
                });
            value.WriteTo(writer);
            writer.Flush();
        }
        catch (ProtocolJsonLimitException exception)
        {
            errors.Add(new(path, exception.Code, exception.Message));
        }
        catch (Exception exception)
            when (exception is JsonException
                  or InvalidOperationException)
        {
            errors.Add(new(
                path,
                "json_depth_exceeded",
                "JSON depth exceeds "
                + $"{ProtocolLimits.MaxProtocolJsonDepth}."));
        }

        try
        {
            var state = new ProtocolJsonInspectionState();
            InspectJson(value, 1, state);
        }
        catch (ProtocolJsonLimitException exception)
        {
            errors.Add(new(path, exception.Code, exception.Message));
        }
    }

    private static void InspectJson(
        JsonElement value,
        int depth,
        ProtocolJsonInspectionState state)
    {
        if (depth > ProtocolLimits.MaxProtocolJsonDepth)
        {
            throw new ProtocolJsonLimitException(
                "json_depth_exceeded",
                "JSON depth exceeds "
                + $"{ProtocolLimits.MaxProtocolJsonDepth}.");
        }

        state.Nodes++;
        if (state.Nodes > ProtocolLimits.MaxProtocolJsonNodes)
        {
            throw new ProtocolJsonLimitException(
                "json_nodes_exceeded",
                "JSON node count exceeds "
                + $"{ProtocolLimits.MaxProtocolJsonNodes}.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    var count = 0;
                    foreach (var property in value.EnumerateObject())
                    {
                        count++;
                        if (count
                            > ProtocolLimits.MaxProtocolJsonContainerItems)
                        {
                            throw new ProtocolJsonLimitException(
                                "json_container_items_exceeded",
                                "A JSON object exceeds "
                                + $"{ProtocolLimits.MaxProtocolJsonContainerItems} properties.");
                        }

                        ValidateJsonStringBytes(property.Name);
                        if (!names.Add(property.Name))
                        {
                            throw new ProtocolJsonLimitException(
                                "json_duplicate_property",
                                "Duplicate JSON properties are not allowed.");
                        }

                        InspectJson(property.Value, depth + 1, state);
                    }

                    break;
                }
            case JsonValueKind.Array:
                {
                    var count = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        count++;
                        if (count
                            > ProtocolLimits.MaxProtocolJsonContainerItems)
                        {
                            throw new ProtocolJsonLimitException(
                                "json_container_items_exceeded",
                                "A JSON array exceeds "
                                + $"{ProtocolLimits.MaxProtocolJsonContainerItems} items.");
                        }

                        InspectJson(item, depth + 1, state);
                    }

                    break;
                }
            case JsonValueKind.String:
                ValidateJsonStringBytes(value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw new ProtocolJsonLimitException(
                    "json_kind_unsupported",
                    "The JSON value kind is not supported.");
        }
    }

    private static void ValidateJsonStringBytes(string value)
    {
        int utf8Bytes;
        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ProtocolJsonLimitException(
                "json_invalid_unicode",
                "A JSON string contains invalid Unicode.");
        }

        if (utf8Bytes > ProtocolLimits.MaxProtocolJsonStringUtf8Bytes)
        {
            throw new ProtocolJsonLimitException(
                "json_string_bytes_exceeded",
                "A JSON string exceeds "
                + $"{ProtocolLimits.MaxProtocolJsonStringUtf8Bytes} UTF-8 bytes.");
        }
    }

    private static void Validate(
        AgentBudget value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (value.MaxTurns < 1)
        {
            errors.Add(new(
                path + ".maxTurns",
                "out_of_range",
                "maxTurns must be positive."));
        }

        if (value.MaxDurationMs < 1)
        {
            errors.Add(new(
                path + ".maxDurationMs",
                "out_of_range",
                "maxDurationMs must be positive."));
        }

        if (value.MaxTokens < 1)
        {
            errors.Add(new(
                path + ".maxTokens",
                "out_of_range",
                "maxTokens must be positive."));
        }

        NonNegativeDecimal(
            value.MaxCostUsd,
            path + ".maxCostUsd",
            errors);

        if (value.MaxActions < 0)
        {
            errors.Add(new(
                path + ".maxActions",
                "out_of_range",
                "maxActions must not be negative."));
        }
    }

    private static void Validate(
        AgentUsage value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        NonNegative(value.Turns, path + ".turns", errors);
        NonNegative(value.DurationMs, path + ".durationMs", errors);
        NonNegative(value.InputTokens, path + ".inputTokens", errors);
        NonNegative(value.OutputTokens, path + ".outputTokens", errors);
        NonNegativeDecimal(value.CostUsd, path + ".costUsd", errors);
        NonNegative(
            value.ProviderUsageSamples,
            path + ".providerUsageSamples",
            errors);
        if (value.CacheReadTokens.HasValue)
        {
            NonNegative(
                value.CacheReadTokens.Value,
                path + ".cacheReadTokens",
                errors);
        }

        if (value.CacheWriteTokens.HasValue)
        {
            NonNegative(
                value.CacheWriteTokens.Value,
                path + ".cacheWriteTokens",
                errors);
        }

        if (value.CacheMissTokens.HasValue)
        {
            NonNegative(
                value.CacheMissTokens.Value,
                path + ".cacheMissTokens",
                errors);
        }

        if (value.ReasoningTokens.HasValue)
        {
            NonNegative(
                value.ReasoningTokens.Value,
                path + ".reasoningTokens",
                errors);
        }

        if (value.ProviderTotalTokens.HasValue)
        {
            NonNegative(
                value.ProviderTotalTokens.Value,
                path + ".providerTotalTokens",
                errors);
        }

        if (!string.Equals(
                value.Availability,
                UsageAvailabilityStates.CostAvailable,
                StringComparison.Ordinal)
            && !string.Equals(
                value.Availability,
                UsageAvailabilityStates.CostUnavailable,
                StringComparison.Ordinal))
        {
            errors.Add(new(
                path + ".availability",
                "unsupported_value",
                "availability must identify whether provider cost is available."));
        }

        if (value.CacheReadTokens.HasValue
            && value.CacheMissTokens.HasValue
            && (long)value.CacheReadTokens.Value
               + value.CacheMissTokens.Value
               != value.InputTokens)
        {
            errors.Add(new(
                path + ".cacheMissTokens",
                "inconsistent_value",
                "Available cache-read and cache-miss counts must sum to inputTokens."));
        }

        if (value.ProviderUsageSamples == 0
            && (value.CacheReadTokens.HasValue
                || value.CacheWriteTokens.HasValue
                || value.CacheMissTokens.HasValue
                || value.ReasoningTokens.HasValue
                || value.ProviderTotalTokens.HasValue
                || string.Equals(
                    value.Availability,
                    UsageAvailabilityStates.CostUnavailable,
                    StringComparison.Ordinal)))
        {
            errors.Add(new(
                path + ".providerUsageSamples",
                "inconsistent_value",
                "Extended provider usage requires at least one provider usage sample."));
        }

        NonNegative(value.Actions, path + ".actions", errors);
        NonNegative(
            value.UnaccountedProviderAttempts,
            path + ".unaccountedProviderAttempts",
            errors);
        if (value.HasUnaccountedUsage
            != (value.UnaccountedProviderAttempts > 0))
        {
            errors.Add(new(
                path + ".hasUnaccountedUsage",
                "inconsistent_value",
                "hasUnaccountedUsage must match the unaccounted provider-attempt count."));
        }
    }

    private static void ValidateVersion(
        VersionedProtocolObject value,
        ICollection<ProtocolValidationError> errors)
    {
        if (!string.Equals(
                value.ProtocolVersion,
                ProtocolConstants.ProtocolVersion,
                StringComparison.Ordinal))
        {
            errors.Add(new(
                "$.protocolVersion",
                "unsupported_version",
                $"Expected protocolVersion {ProtocolConstants.ProtocolVersion}."));
        }

        if (!string.Equals(
                value.SchemaVersion,
                ProtocolConstants.SchemaVersion,
                StringComparison.Ordinal))
        {
            errors.Add(new(
                "$.schemaVersion",
                "unsupported_version",
                $"Expected schemaVersion {ProtocolConstants.SchemaVersion}."));
        }
    }

    private static void Required(
        string? value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(path, "required", "A non-empty value is required."));
        }
    }

    private static void RequiredId(
        string? value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        Required(value, path, errors);
        if (!string.IsNullOrWhiteSpace(value) && value.Length > 128)
        {
            errors.Add(new(
                path,
                "out_of_range",
                "The identifier cannot exceed 128 characters."));
        }
        else if (!string.IsNullOrWhiteSpace(value) && !IdPattern.IsMatch(value))
        {
            errors.Add(new(path, "invalid_id", "The identifier contains unsupported characters."));
        }
    }

    private static void RequiredName(
        string? value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        Required(value, path, errors);
        OptionalUnicodeScalars(
            value,
            96,
            path,
            requireNonEmpty: false,
            errors);
        if (!string.IsNullOrWhiteSpace(value)
            && value.Length <= 96
            && !NamePattern.IsMatch(value))
        {
            errors.Add(new(
                path,
                "invalid_name",
                "The name contains unsupported characters."));
        }
    }

    private static void OptionalId(
        string? value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (value is not null)
        {
            RequiredId(value, path, errors);
        }
    }

    private static void OptionalBounded(
        string? value,
        int maximumLength,
        string path,
        bool requireNonEmpty,
        ICollection<ProtocolValidationError> errors)
    {
        OptionalUnicodeScalars(
            value,
            maximumLength,
            path,
            requireNonEmpty,
            errors);
    }

    private static void OptionalUnicodeScalars(
        string? value,
        int maximumLength,
        string path,
        bool requireNonEmpty,
        ICollection<ProtocolValidationError> errors)
    {
        if (value is null)
        {
            return;
        }

        if (requireNonEmpty && value.Length == 0)
        {
            errors.Add(new(
                path,
                "required",
                "A non-empty value is required."));
            return;
        }

        var scalars = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    errors.Add(new(
                        path,
                        "invalid_unicode",
                        "The value contains invalid Unicode."));
                    return;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                errors.Add(new(
                    path,
                    "invalid_unicode",
                    "The value contains invalid Unicode."));
                return;
            }

            scalars++;
            if (scalars > maximumLength)
            {
                errors.Add(new(
                    path,
                    "out_of_range",
                    $"The value cannot exceed {maximumLength} Unicode scalar values."));
                return;
            }
        }
    }

    private static void ValidateUniqueIds(
        IReadOnlyList<string>? values,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (values is null)
        {
            errors.Add(new(
                path,
                "required",
                "A collection is required."));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var itemPath = path + "[" + index + "]";
            RequiredId(values[index], itemPath, errors);
            if (!seen.Add(values[index]))
            {
                errors.Add(new(
                    itemPath,
                    "duplicate_value",
                    "The collection must contain unique values."));
            }
        }
    }

    private static void ValidateUniqueBoundedStrings(
        IReadOnlyList<string>? values,
        int maximumLength,
        string path,
        bool requireNonEmpty,
        ICollection<ProtocolValidationError> errors)
    {
        ValidateBoundedStrings(
            values,
            int.MaxValue,
            maximumLength,
            path,
            requireNonEmpty,
            errors,
            requireUnique: true);
    }

    private static void ValidateBoundedStrings(
        IReadOnlyList<string>? values,
        int maximumItems,
        int maximumLength,
        string path,
        bool requireNonEmpty,
        ICollection<ProtocolValidationError> errors,
        bool requireUnique = false)
    {
        if (values is null)
        {
            errors.Add(new(
                path,
                "invalid_type",
                "A string collection is required."));
            return;
        }

        if (values.Count > maximumItems)
        {
            errors.Add(new(
                path,
                "out_of_range",
                $"The collection cannot exceed {maximumItems} items."));
        }

        var seen = requireUnique
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        var count = Math.Min(values.Count, maximumItems);
        for (var index = 0; index < count; index++)
        {
            var itemPath = path + "[" + index + "]";
            if (values[index] is null)
            {
                errors.Add(new(
                    itemPath,
                    "invalid_type",
                    "A string value is required."));
                continue;
            }

            OptionalUnicodeScalars(
                values[index],
                maximumLength,
                itemPath,
                requireNonEmpty,
                errors);
            if (seen is not null && !seen.Add(values[index]))
            {
                errors.Add(new(
                    itemPath,
                    "duplicate_value",
                    "The collection must contain unique values."));
            }
        }
    }

    private static void NonNegative(
        long value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (value < 0)
        {
            errors.Add(new(
                path,
                "out_of_range",
                "The value must not be negative."));
        }
    }

    private static void NonNegativeDecimal(
        string? value,
        string path,
        ICollection<ProtocolValidationError> errors)
    {
        if (!IsCanonicalNonNegativeDecimal(value))
        {
            errors.Add(new(
                path,
                "invalid_decimal",
                "The value must be a non-negative decimal string."));
        }
    }

    private static bool IsCanonicalNonNegativeDecimal(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var index = 0;
        if (value[0] == '0')
        {
            index = 1;
            if (value.Length > 1 && value[1] != '.')
            {
                return false;
            }
        }
        else if (value[0] is >= '1' and <= '9')
        {
            index = 1;
            while (index < value.Length
                   && value[index] is >= '0' and <= '9')
            {
                index++;
            }
        }
        else
        {
            return false;
        }

        if (index == value.Length)
        {
            return true;
        }

        if (value[index] != '.' || index == value.Length - 1)
        {
            return false;
        }

        index++;
        while (index < value.Length)
        {
            if (value[index] is not (>= '0' and <= '9'))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private sealed class ProtocolJsonInspectionState
    {
        public int Nodes { get; set; }
    }

    private sealed class ProtocolJsonLimitException : Exception
    {
        public ProtocolJsonLimitException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    private sealed class ProtocolCountingBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private const int DefaultSizeHint = 256;
        private const int WriterSlackBytes = 4_096;

        private readonly int _maximumBytes;
        private readonly int _maximumBufferBytes;
        private byte[]? _buffer;
        private int _written;

        public ProtocolCountingBufferWriter(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _maximumBufferBytes = checked(
                maximumBytes + WriterSlackBytes);
        }

        public void Advance(int count)
        {
            if (count < 0
                || _buffer is null
                || count > _buffer.Length
                || count > _maximumBytes - _written)
            {
                ThrowByteLimit();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer!;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer!;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    buffer,
                    clearArray: true);
            }
        }

        private void EnsureBuffer(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sizeHint));
            }

            var required = sizeHint == 0
                ? DefaultSizeHint
                : sizeHint;
            if (required > _maximumBufferBytes)
            {
                ThrowByteLimit();
            }

            if (_buffer is not null
                && _buffer.Length >= required)
            {
                return;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(required);
            var previous = _buffer;
            _buffer = replacement;
            if (previous is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    previous,
                    clearArray: true);
            }
        }

        private static void ThrowByteLimit()
        {
            throw new ProtocolJsonLimitException(
                "json_bytes_exceeded",
                "JSON content exceeds "
                + $"{ProtocolLimits.MaxProtocolJsonUtf8Bytes} UTF-8 bytes.");
        }
    }

    private static void ThrowIfInvalid(IReadOnlyList<ProtocolValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        throw new JsonException($"{errors[0].Path}: {errors[0].Message}");
    }
}
