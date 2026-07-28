namespace GameAgent.Protocol;

public static class ProtocolConstants
{
    public const string ProtocolVersion = "0.2";
    public const string SchemaVersion = "0.2";
}

public static class RunStates
{
    public const string Queued = "queued";
    public const string Preparing = "preparing";
    public const string Running = "running";
    public const string Cancelling = "cancelling";
    public const string Interrupting = "interrupting";
    public const string WaitingForAction = "waiting_for_action";
    public const string Reconciling = "reconciling";
    public const string Completed = "completed";
    public const string BudgetExhausted = "budget_exhausted";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

public static class CompletionIntents
{
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
    public const string Failed = "failed";
}

public static class ToolEffects
{
    public const string PureRead = "pure_read";
    public const string AgentLocalWrite = "agent_local_write";
    public const string WorldCommand = "world_command";
    public const string ExternalWrite = "external_write";
}

public static class ThreadAffinities
{
    public const string AnyThread = "any_thread";
    public const string EngineMainThread = "engine_main_thread";
    public const string HostManaged = "host_managed";
}

public static class ToolRetryPolicies
{
    public const string Never = "never";
    public const string SafeRead = "safe_read";
    public const string Idempotent = "idempotent";
}

public static class ToolIdempotencyPolicies
{
    public const string Required = "required";
    public const string BestEffort = "best_effort";
    public const string None = "none";
}

public static class ToolVisibilities
{
    public const string Direct = "direct";
    public const string Deferred = "deferred";
    public const string Internal = "internal";
}

public static class ReceiptStatuses
{
    public const string Succeeded = "succeeded";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

public static class EventDurabilities
{
    public const string Durable = "durable";
    public const string Ephemeral = "ephemeral";
}

public static class RuntimeEventKinds
{
    public const string RunStarted = "run.started";
    public const string RunCompleted = "run.completed";
    public const string RunInterrupted = "run.interrupted";
    public const string RunFailed = "run.failed";
    public const string RunCancelled = "run.cancelled";
    public const string RunBudgetExhausted = "run.budget_exhausted";
    public const string RunCheckpoint = "run.checkpoint";
    public const string RunInputCaptured = "run.input_captured";
    public const string TurnStarted = "turn.started";
    public const string TurnCompleted = "turn.completed";
    public const string TurnSnapshot = "turn.snapshot";
    public const string TranscriptMessage = "transcript.message";
    public const string AssistantDelta = "assistant.delta";
    public const string AssistantCompleted = "assistant.completed";
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string ToolFailed = "tool.failed";
    public const string ToolDisclosureChanged = "tool.disclosure_changed";
    public const string ActionRequested = "action.requested";
    public const string ActionReceived = "action.received";
    public const string ActionReconciling = "action.reconciling";
    public const string ProviderRetry = "provider.retry";
    public const string ProviderFallback = "provider.fallback";
    public const string ProviderDispatchStarted = "provider.dispatch_started";
    public const string ProviderDispatchKnownZero =
        "provider.dispatch_known_zero";
    public const string ProviderUsageUncertain = "provider.usage_uncertain";
    public const string ProviderResultCommitted = "provider.result_committed";
    public const string ProviderResultDiscarded = "provider.result_discarded";
    public const string ControlReceived = "control.received";
    public const string BudgetUpdated = "budget.updated";
}
