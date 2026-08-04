using System.Collections.ObjectModel;
using System.Globalization;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class RuntimeReplayProviderRecord
{
    internal RuntimeReplayProviderRecord(
        string attemptKey,
        string? providerId,
        string? modelId,
        string? transportDialect,
        string? capabilityDigest,
        string? routeDigest,
        string? routePolicyVersion,
        string? routePolicyDigest,
        long dispatchSequence,
        long? terminalSequence,
        string? terminalKind,
        long usageSamples,
        long inputTokens,
        long outputTokens,
        string costUsd)
    {
        AttemptKey = attemptKey;
        ProviderId = providerId;
        ModelId = modelId;
        TransportDialect = transportDialect;
        CapabilityDigest = capabilityDigest;
        RouteDigest = routeDigest;
        RoutePolicyVersion = routePolicyVersion;
        RoutePolicyDigest = routePolicyDigest;
        DispatchSequence = dispatchSequence;
        TerminalSequence = terminalSequence;
        TerminalKind = terminalKind;
        UsageSamples = usageSamples;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostUsd = costUsd;
    }

    public string AttemptKey { get; }

    public string? ProviderId { get; }

    public string? ModelId { get; }

    public string? TransportDialect { get; }

    public string? CapabilityDigest { get; }

    public string? RouteDigest { get; }

    public string? RoutePolicyVersion { get; }

    public string? RoutePolicyDigest { get; }

    public long DispatchSequence { get; }

    public long? TerminalSequence { get; }

    public string? TerminalKind { get; }

    public long UsageSamples { get; }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public string CostUsd { get; }
}

public sealed class RuntimeReplayHostRecord
{
    internal RuntimeReplayHostRecord(
        string operationId,
        string? toolCallId,
        string? actionName,
        string? actionVersion,
        long? requestSequence,
        long? receiptSequence,
        int receiptCount,
        long? receiptRevision,
        string? receiptStatus,
        string? argumentsDigest,
        string? requestDigest,
        string? receiptDigest)
    {
        OperationId = operationId;
        ToolCallId = toolCallId;
        ActionName = actionName;
        ActionVersion = actionVersion;
        RequestSequence = requestSequence;
        ReceiptSequence = receiptSequence;
        ReceiptCount = receiptCount;
        ReceiptRevision = receiptRevision;
        ReceiptStatus = receiptStatus;
        ArgumentsDigest = argumentsDigest;
        RequestDigest = requestDigest;
        ReceiptDigest = receiptDigest;
    }

    public string OperationId { get; }

    public string? ToolCallId { get; }

    public string? ActionName { get; }

    public string? ActionVersion { get; }

    public long? RequestSequence { get; }

    public long? ReceiptSequence { get; }

    public int ReceiptCount { get; }

    public long? ReceiptRevision { get; }

    public string? ReceiptStatus { get; }

    public string? ArgumentsDigest { get; }

    public string? RequestDigest { get; }

    public string? ReceiptDigest { get; }
}

public sealed class RuntimeReplayClockRecord
{
    internal RuntimeReplayClockRecord(
        string eventId,
        long sequence,
        DateTimeOffset timestamp)
    {
        EventId = eventId;
        Sequence = sequence;
        Timestamp = timestamp;
    }

    public string EventId { get; }

    public long Sequence { get; }

    public DateTimeOffset Timestamp { get; }
}

public sealed class RuntimeReplayIdentityRecord
{
    internal RuntimeReplayIdentityRecord(
        string eventId,
        string? runId,
        string? turnId,
        string? attemptId,
        string? streamAttemptId,
        long runtimeGeneration,
        long sequence,
        string kind)
    {
        EventId = eventId;
        RunId = runId;
        TurnId = turnId;
        AttemptId = attemptId;
        StreamAttemptId = streamAttemptId;
        RuntimeGeneration = runtimeGeneration;
        Sequence = sequence;
        Kind = kind;
    }

    public string EventId { get; }

    public string? RunId { get; }

    public string? TurnId { get; }

    public string? AttemptId { get; }

    public string? StreamAttemptId { get; }

    public long RuntimeGeneration { get; }

    public long Sequence { get; }

    public string Kind { get; }
}

public sealed class RuntimeReplayResult
{
    internal RuntimeReplayResult(
        bool passed,
        IReadOnlyList<string> failureCodes,
        IReadOnlyList<RuntimeReplayProviderRecord> providerRecords,
        IReadOnlyList<RuntimeReplayHostRecord> hostRecords,
        IReadOnlyList<RuntimeReplayClockRecord> clockRecords,
        IReadOnlyList<RuntimeReplayIdentityRecord> identityRecords,
        string trajectoryDigest,
        string replayDigest)
    {
        Passed = passed;
        FailureCodes = failureCodes;
        ProviderRecords = providerRecords;
        HostRecords = hostRecords;
        ClockRecords = clockRecords;
        IdentityRecords = identityRecords;
        TrajectoryDigest = trajectoryDigest;
        ReplayDigest = replayDigest;
    }

    public bool Passed { get; }

    public IReadOnlyList<string> FailureCodes { get; }

    public IReadOnlyList<RuntimeReplayProviderRecord> ProviderRecords
    {
        get;
    }

    public IReadOnlyList<RuntimeReplayHostRecord> HostRecords { get; }

    public IReadOnlyList<RuntimeReplayClockRecord> ClockRecords { get; }

    public IReadOnlyList<RuntimeReplayIdentityRecord> IdentityRecords
    {
        get;
    }

    public int ProviderAttemptsReplayed => ProviderRecords.Count;

    public int HostActionsReplayed => HostRecords.Count;

    public int ClockSamplesReplayed => ClockRecords.Count;

    public int IdentitiesReplayed => IdentityRecords.Count;

    public string TrajectoryDigest { get; }

    public string ReplayDigest { get; }
}

/// <summary>
/// Verifies a recorded trace without invoking providers, host actions,
/// clocks, or ID generators. Replay records are immutable observations.
/// </summary>
public sealed class RecordedRuntimeReplayHarness
{
    private readonly RuntimeTraceAnalysisOptions _options;

    public RecordedRuntimeReplayHarness(
        RuntimeTraceAnalysisOptions? options = null)
    {
        _options = (options ?? new RuntimeTraceAnalysisOptions()).Snapshot();
    }

    public RuntimeReplayResult Replay(IEnumerable<RuntimeEvent> events)
    {
        var analysis = new RuntimeTraceAnalyzer(_options).Analyze(events);
        return Replay(analysis);
    }

    public RuntimeReplayResult Replay(RuntimeTraceAnalysis analysis)
    {
        if (analysis is null)
        {
            throw new ArgumentNullException(nameof(analysis));
        }

        var trajectory = analysis.Trajectory;
        var providers = trajectory.ProviderAttempts
            .Select(
                item => new RuntimeReplayProviderRecord(
                    item.AttemptKey,
                    item.ProviderId,
                    item.ModelId,
                    item.TransportDialect,
                    item.CapabilityDigest,
                    item.RouteDigest,
                    item.RoutePolicyVersion,
                    item.RoutePolicyDigest,
                    item.DispatchSequence,
                    item.TerminalSequence,
                    item.TerminalKind,
                    item.UsageSamples,
                    item.InputTokens,
                    item.OutputTokens,
                    item.CostUsd))
            .ToArray();
        var host = trajectory.Actions
            .Select(
                item => new RuntimeReplayHostRecord(
                    item.OperationId,
                    item.ToolCallId,
                    item.ActionName,
                    item.ActionVersion,
                    item.RequestSequence,
                    item.ReceiptSequence,
                    item.ReceiptCount,
                    item.ReceiptRevision,
                    item.ReceiptStatus,
                    item.ArgumentsDigest,
                    item.RequestDigest,
                    item.ReceiptDigest))
            .ToArray();
        var clocks = trajectory.Events
            .Select(
                item => new RuntimeReplayClockRecord(
                    item.EventId,
                    item.Sequence,
                    item.Timestamp))
            .ToArray();
        var identities = trajectory.Events
            .Select(
                item => new RuntimeReplayIdentityRecord(
                    item.EventId,
                    item.RunId,
                    item.TurnId,
                    item.AttemptId,
                    item.StreamAttemptId,
                    item.RuntimeGeneration,
                    item.Sequence,
                    item.Kind))
            .ToArray();
        var failures = new ReadOnlyCollection<string>(
            trajectory.AssertionFailureCodes
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray());
        var replayDigest = ComputeDigest(
            trajectory.Digest,
            providers,
            host,
            clocks,
            identities,
            failures);
        return new RuntimeReplayResult(
            failures.Count == 0,
            failures,
            new ReadOnlyCollection<RuntimeReplayProviderRecord>(providers),
            new ReadOnlyCollection<RuntimeReplayHostRecord>(host),
            new ReadOnlyCollection<RuntimeReplayClockRecord>(clocks),
            new ReadOnlyCollection<RuntimeReplayIdentityRecord>(identities),
            trajectory.Digest,
            replayDigest);
    }

    private static string ComputeDigest(
        string trajectoryDigest,
        IEnumerable<RuntimeReplayProviderRecord> providers,
        IEnumerable<RuntimeReplayHostRecord> host,
        IEnumerable<RuntimeReplayClockRecord> clocks,
        IEnumerable<RuntimeReplayIdentityRecord> identities,
        IReadOnlyList<string> failures)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "recorded-runtime-replay-v1");
        digest.Add("trajectory", trajectoryDigest);
        foreach (var item in providers)
        {
            digest.Add("provider.key", item.AttemptKey);
            digest.Add("provider.id", item.ProviderId);
            digest.Add("provider.model", item.ModelId);
            digest.Add("provider.dialect", item.TransportDialect);
            digest.Add("provider.capability", item.CapabilityDigest);
            digest.Add("provider.route", item.RouteDigest);
            digest.Add(
                "provider.routePolicyVersion",
                item.RoutePolicyVersion);
            digest.Add(
                "provider.routePolicyDigest",
                item.RoutePolicyDigest);
            digest.Add("provider.dispatch", item.DispatchSequence);
            digest.Add("provider.terminal", item.TerminalSequence ?? -1);
            digest.Add("provider.kind", item.TerminalKind);
            digest.Add("provider.usageSamples", item.UsageSamples);
            digest.Add("provider.inputTokens", item.InputTokens);
            digest.Add("provider.outputTokens", item.OutputTokens);
            digest.Add("provider.costUsd", item.CostUsd);
        }

        foreach (var item in host)
        {
            digest.Add("host.operation", item.OperationId);
            digest.Add("host.tool", item.ToolCallId);
            digest.Add("host.action", item.ActionName);
            digest.Add("host.actionVersion", item.ActionVersion);
            digest.Add("host.request", item.RequestSequence ?? -1);
            digest.Add("host.receipt", item.ReceiptSequence ?? -1);
            digest.Add("host.receiptCount", item.ReceiptCount);
            digest.Add("host.receiptRevision", item.ReceiptRevision ?? -1);
            digest.Add("host.status", item.ReceiptStatus);
            digest.Add("host.arguments", item.ArgumentsDigest);
            digest.Add("host.requestDigest", item.RequestDigest);
            digest.Add("host.digest", item.ReceiptDigest);
        }

        foreach (var item in clocks)
        {
            digest.Add("clock.event", item.EventId);
            digest.Add("clock.sequence", item.Sequence);
            digest.Add(
                "clock.timestamp",
                item.Timestamp.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture));
        }

        foreach (var item in identities)
        {
            digest.Add("identity.event", item.EventId);
            digest.Add("identity.run", item.RunId);
            digest.Add("identity.turn", item.TurnId);
            digest.Add("identity.attempt", item.AttemptId);
            digest.Add("identity.stream", item.StreamAttemptId);
            digest.Add("identity.generation", item.RuntimeGeneration);
            digest.Add("identity.sequence", item.Sequence);
            digest.Add("identity.kind", item.Kind);
        }

        digest.Add("failures", failures);
        return digest.Finish();
    }
}

public sealed class RuntimeReplayVerifier
{
    private readonly RecordedRuntimeReplayHarness _harness;

    public RuntimeReplayVerifier(
        RuntimeTraceAnalysisOptions? options = null)
    {
        _harness = new RecordedRuntimeReplayHarness(options);
    }

    public RuntimeReplayResult Verify(IEnumerable<RuntimeEvent> events)
    {
        return _harness.Replay(events);
    }

    public RuntimeReplayResult Verify(RuntimeTraceAnalysis analysis)
    {
        return _harness.Replay(analysis);
    }
}
