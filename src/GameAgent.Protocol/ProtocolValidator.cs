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
    private static readonly Regex IdPattern = new(
        "^[A-Za-z0-9._:-]+$",
        RegexOptions.CultureInvariant);

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
        ValidateUniqueIds(value.SubjectIds, "$.subjectIds", errors);

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
            ValidateUniqueIds(
                value.Visibility.AudienceIds,
                "$.visibility.audienceIds",
                errors);
        }

        if (value.Extensions is null)
        {
            errors.Add(new(
                "$.extensions",
                "invalid_type",
                "extensions must be an object when present."));
        }

        if (value.Payload.HasValue == (value.ResourceRef is not null))
        {
            errors.Add(new(
                "$",
                "exactly_one_content",
                "Exactly one of payload or resourceRef is required."));
        }

        if (value.ResourceRef is not null)
        {
            Required(value.ResourceRef.Uri, "$.resourceRef.uri", errors);
            Required(
                value.ResourceRef.MediaType,
                "$.resourceRef.mediaType",
                errors);
            if (value.ResourceRef.SizeBytes < 0)
            {
                errors.Add(new(
                    "$.resourceRef.sizeBytes",
                    "out_of_range",
                    "sizeBytes must not be negative."));
            }
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
        Required(value.Name, "$.name", errors);
        Required(value.Version, "$.version", errors);
        Required(value.Description, "$.description", errors);

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
        Required(value.ActionName, "$.actionName", errors);
        Required(value.ActionVersion, "$.actionVersion", errors);
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

        if (value.AuthoritativeObservations is null)
        {
            errors.Add(new(
                "$.authoritativeObservations",
                "invalid_type",
                "authoritativeObservations must be an array when present."));
        }

        if (value.Extensions is null)
        {
            errors.Add(new(
                "$.extensions",
                "invalid_type",
                "extensions must be an object when present."));
        }

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
        OptionalBounded(
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

        if (value.Extensions is null)
        {
            errors.Add(new(
                "$.extensions",
                "invalid_type",
                "extensions must be an object when present."));
        }

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

    public static void EnsureValid(ActionRequest value)
    {
        ThrowIfInvalid(Validate(value));
    }

    public static void EnsureValid(ActionReceipt value)
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
        }

        if (value.Length > maximumLength)
        {
            errors.Add(new(
                path,
                "out_of_range",
                $"The value cannot exceed {maximumLength} characters."));
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

    private static void ThrowIfInvalid(IReadOnlyList<ProtocolValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        throw new JsonException($"{errors[0].Path}: {errors[0].Message}");
    }
}
