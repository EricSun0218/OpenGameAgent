using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public interface IConversationContextEngine
{
    string EngineId { get; }

    string Version { get; }

    bool CleanupCompleted { get; }

    ValueTask<ConversationContextView> PrepareAsync(
        string runId,
        string turnId,
        IReadOnlyList<NormalizedMessage> transcript,
        IReadOnlyCollection<string>? stablePrefixMessageIds = null,
        CancellationToken cancellationToken = default);

    void RegisterCheckpoint(JsonElement checkpoint);

    /// <summary>
    /// Returns false only when bounded lifecycle cancellation admission was
    /// unavailable and shutdown should be retried.
    /// </summary>
    ValueTask<bool> StopAsync();
}

public sealed class ConversationContextOptions
{
    private const int Mebibyte = 1_048_576;

    public bool Enabled { get; set; } = true;

    public int MaxRequestMessages { get; set; } = 256;

    public int MaxRequestUtf8Bytes { get; set; } = 786_432;

    /// <summary>
    /// Hard admission limit applied to the caller-owned transcript before
    /// compaction or serialization.
    /// </summary>
    public int MaxInputMessages { get; set; } = 16_384;

    /// <summary>
    /// Hard aggregate limit for the encoded caller-owned transcript.
    /// </summary>
    public int MaxInputUtf8Bytes { get; set; } = 64 * Mebibyte;

    /// <summary>
    /// Hard aggregate JSON-node limit for the caller-owned transcript,
    /// including normalized-message envelopes and nested JSON parts.
    /// </summary>
    public int MaxInputJsonNodes { get; set; } = 1_048_576;

    /// <summary>
    /// Maximum number of caller-declared stable message identifiers.
    /// </summary>
    public int MaxStablePrefixMessageIds { get; set; } = 16_384;

    /// <summary>
    /// Maximum aggregate UTF-8 bytes in caller-declared stable identifiers.
    /// </summary>
    public int MaxStablePrefixUtf8Bytes { get; set; } = 2 * Mebibyte;

    public int RecentMessagesToKeep { get; set; } = 32;

    public int MaxSummaryUtf8Bytes { get; set; } = 32_768;

    public TimeSpan CompactionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromMinutes(1);

    public int MaxConcurrentCompactions { get; set; } = 4;

    public TimeSpan DetachedShutdownTimeout { get; set; } =
        TimeSpan.FromSeconds(5);

    internal ConversationContextOptions Snapshot()
    {
        if (MaxRequestMessages is < 4 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRequestMessages));
        }

        if (MaxRequestUtf8Bytes is < 4_096 or > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRequestUtf8Bytes));
        }

        if (MaxInputMessages < MaxRequestMessages
            || MaxInputMessages > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInputMessages));
        }

        if (MaxInputUtf8Bytes < MaxRequestUtf8Bytes
            || MaxInputUtf8Bytes > 256 * Mebibyte)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInputUtf8Bytes));
        }

        if (MaxInputJsonNodes is < 16 or > 16 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInputJsonNodes));
        }

        if (MaxStablePrefixMessageIds is < 0 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxStablePrefixMessageIds));
        }

        if (MaxStablePrefixUtf8Bytes is < 0 or > 16 * Mebibyte)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxStablePrefixUtf8Bytes));
        }

        if (RecentMessagesToKeep is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(RecentMessagesToKeep));
        }

        if (MaxSummaryUtf8Bytes is < 256 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSummaryUtf8Bytes));
        }

        if (MaxSummaryUtf8Bytes >= MaxRequestUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSummaryUtf8Bytes),
                "The summary budget must be smaller than the request budget.");
        }

        if (RecentMessagesToKeep >= MaxRequestMessages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RecentMessagesToKeep),
                "The recent-message window must leave room for a summary.");
        }

        if (CompactionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CompactionTimeout));
        }

        if (FailureCooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FailureCooldown));
        }

        if (MaxConcurrentCompactions is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentCompactions));
        }

        if (DetachedShutdownTimeout <= TimeSpan.Zero
            || DetachedShutdownTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(DetachedShutdownTimeout));
        }

        return new ConversationContextOptions
        {
            Enabled = Enabled,
            MaxRequestMessages = MaxRequestMessages,
            MaxRequestUtf8Bytes = MaxRequestUtf8Bytes,
            MaxInputMessages = MaxInputMessages,
            MaxInputUtf8Bytes = MaxInputUtf8Bytes,
            MaxInputJsonNodes = MaxInputJsonNodes,
            MaxStablePrefixMessageIds = MaxStablePrefixMessageIds,
            MaxStablePrefixUtf8Bytes = MaxStablePrefixUtf8Bytes,
            RecentMessagesToKeep = RecentMessagesToKeep,
            MaxSummaryUtf8Bytes = MaxSummaryUtf8Bytes,
            CompactionTimeout = CompactionTimeout,
            FailureCooldown = FailureCooldown,
            MaxConcurrentCompactions = MaxConcurrentCompactions,
            DetachedShutdownTimeout = DetachedShutdownTimeout
        };
    }
}

public sealed class ConversationCompactionRequest
{
    public ConversationCompactionRequest(
        string runId,
        string turnId,
        IReadOnlyList<NormalizedMessage> messages,
        string sourceDigest,
        int maxSummaryUtf8Bytes)
        : this(
            runId,
            turnId,
            messages,
            sourceDigest,
            maxSummaryUtf8Bytes,
            new ConversationContextOptions().Snapshot(),
            messagesAreAdmittedSnapshots: false)
    {
    }

    internal ConversationCompactionRequest(
        string runId,
        string turnId,
        IReadOnlyList<NormalizedMessage> messages,
        string sourceDigest,
        int maxSummaryUtf8Bytes,
        ConversationContextOptions inputOptions,
        bool messagesAreAdmittedSnapshots)
    {
        RunId = RuntimeGuard.RequiredUtf8(runId, 128, nameof(runId));
        TurnId = RuntimeGuard.RequiredUtf8(turnId, 128, nameof(turnId));
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }
        SourceDigest = RuntimeGuard.RequiredUtf8(
            sourceDigest,
            128,
            nameof(sourceDigest));
        if (maxSummaryUtf8Bytes < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSummaryUtf8Bytes));
        }

        AdmittedMessages = messagesAreAdmittedSnapshots
            ? new ReadOnlyCollection<NormalizedMessage>(messages.ToArray())
            : ConversationContextManager.SnapshotCompactionMessages(
                messages,
                inputOptions);
        Messages = new ReadOnlyCollection<NormalizedMessage>(
            AdmittedMessages
                .Select(
                    message =>
                        NormalizedMessageJournalCodec.CloneValidated(message))
                .ToArray());
        MaxSummaryUtf8Bytes = maxSummaryUtf8Bytes;
    }

    public string RunId { get; }

    public string TurnId { get; }

    public IReadOnlyList<NormalizedMessage> Messages { get; }

    internal IReadOnlyList<NormalizedMessage> AdmittedMessages { get; }

    public string SourceDigest { get; }

    public int MaxSummaryUtf8Bytes { get; }
}

public sealed class ConversationCompactionResult
{
    public ConversationCompactionResult(
        string summaryText,
        IReadOnlyList<string>? sourceMessageIds,
        string sourceDigest)
    {
        SummaryText = RuntimeGuard.RequiredUtf8(
            summaryText,
            1_048_576,
            nameof(summaryText));
        SourceMessageIds = SnapshotSourceMessageIds(
            sourceMessageIds,
            nameof(sourceMessageIds));
        SourceDigest = RuntimeGuard.RequiredUtf8(
            sourceDigest,
            128,
            nameof(sourceDigest));
    }

    /// <summary>
    /// Untrusted historical data. The runtime, rather than the compactor,
    /// constructs the normalized message and its fixed low-authority envelope.
    /// </summary>
    public string SummaryText { get; }

    /// <summary>
    /// Exact source-message identifiers used to create the summary. Every
    /// identifier is checked against the compaction request before admission.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; }

    public string SourceDigest { get; }

    internal ConversationSummaryQuality.SummaryAnalysis? QualityAnalysis
    {
        get;
        set;
    }

    private static IReadOnlyList<string> SnapshotSourceMessageIds(
        IReadOnlyList<string>? values,
        string parameterName)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var count = values.Count;
        if (count < 0)
        {
            throw new ArgumentException(
                "The source identifier collection returned a negative count.",
                parameterName);
        }
        if (count > 128)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "collection_items_exceeded",
                "The collection exceeds 128 items.");
        }

        var result = new string[count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            string value;
            try
            {
                value = values[index];
            }
            catch (Exception exception)
                when (exception is ArgumentOutOfRangeException
                      or IndexOutOfRangeException)
            {
                throw new ArgumentException(
                    "The source identifier count and indexed contents "
                    + "are inconsistent.",
                    parameterName,
                    exception);
            }

            value = RuntimeGuard.RequiredUtf8(
                value,
                128,
                parameterName);
            if (!seen.Add(value))
            {
                throw new ArgumentException(
                    $"Duplicate value '{value}' is not allowed.",
                    parameterName);
            }
            result[index] = value;
        }

        return new ReadOnlyCollection<string>(result);
    }
}

public interface IConversationCompactor
{
    ValueTask<ConversationCompactionResult> CompactAsync(
        ConversationCompactionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic fallback compactor that needs no model. It emits a bounded
/// typed digest of the removed messages. Games can inject a model-backed
/// compactor without changing the runtime or durable transcript. Every result
/// is independently checked for source lineage, semantic anchors, coverage,
/// and useful byte reclamation before admission. Compactors return only
/// summary data and audited source identifiers; they cannot choose a provider
/// role, message identifier, or tool/reasoning content part.
/// </summary>
public sealed class ExtractiveConversationCompactor : IConversationCompactor
{
    public ValueTask<ConversationCompactionResult> CompactAsync(
        ConversationCompactionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<ConversationCompactionResult>(
            ConversationSummaryQuality.CreateDeterministicResult(
                request,
                analysis: null,
                cancellationToken));
    }
}

internal static class ConversationSummaryEnvelope
{
    private const string ContentType =
        "application/vnd.game-agent.conversation-summary+json";

    public static NormalizedMessage Create(
        ConversationCompactionRequest request,
        ConversationCompactionResult result)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }
        if (!string.Equals(
                result.SourceDigest,
                request.SourceDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The compactor returned a summary for a different source.");
        }

        var sourceIds = request.AdmittedMessages
            .Select(item => item.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var sourceMessageId in result.SourceMessageIds)
        {
            if (!sourceIds.Contains(sourceMessageId))
            {
                throw new InvalidOperationException(
                    "The compactor referenced a message outside its source.");
            }
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contentType", ContentType);
            writer.WriteString("authority", "historical-data");
            writer.WriteString("sourceDigest", request.SourceDigest);
            writer.WriteNumber(
                "sourceMessageCount",
                request.AdmittedMessages.Count);
            writer.WriteStartArray("sourceMessageIds");
            foreach (var sourceMessageId in result.SourceMessageIds)
            {
                writer.WriteStringValue(sourceMessageId);
            }
            writer.WriteEndArray();
            writer.WriteString("summary", result.SummaryText);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new NormalizedMessage
        {
            MessageId = "conversation-summary:"
                        + request.SourceDigest[
                            ..Math.Min(16, request.SourceDigest.Length)],
            Role = NormalizedRoles.User,
            CreatedAt = request.AdmittedMessages.Count == 0
                ? DateTimeOffset.UnixEpoch
                : request.AdmittedMessages.Max(item => item.CreatedAt),
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromJson(document.RootElement)
            }
        };
    }
}

public sealed class ConversationContextReport
{
    public ConversationContextReport(
        int inputMessageCount,
        int outputMessageCount,
        int droppedMessageCount,
        int inputUtf8Bytes,
        int outputUtf8Bytes,
        bool compacted,
        bool compactionFailed,
        bool compactionSkippedByCooldown,
        string sourceDigest,
        string viewDigest)
    {
        if (inputMessageCount < 0
            || outputMessageCount < 0
            || outputMessageCount > inputMessageCount
            || droppedMessageCount < 0
            || inputUtf8Bytes < 0
            || outputUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputMessageCount),
                "Conversation context report counts and sizes are invalid.");
        }

        InputMessageCount = inputMessageCount;
        OutputMessageCount = outputMessageCount;
        DroppedMessageCount = droppedMessageCount;
        InputUtf8Bytes = inputUtf8Bytes;
        OutputUtf8Bytes = outputUtf8Bytes;
        Compacted = compacted;
        CompactionFailed = compactionFailed;
        CompactionSkippedByCooldown = compactionSkippedByCooldown;
        SourceDigest = RuntimeGuard.RequiredUtf8(
            sourceDigest,
            128,
            nameof(sourceDigest));
        ViewDigest = RuntimeGuard.RequiredUtf8(
            viewDigest,
            128,
            nameof(viewDigest));
    }

    public int InputMessageCount { get; }

    public int OutputMessageCount { get; }

    public int DroppedMessageCount { get; }

    public int InputUtf8Bytes { get; }

    public int OutputUtf8Bytes { get; }

    public bool Compacted { get; }

    public bool CompactionFailed { get; }

    public bool CompactionSkippedByCooldown { get; }

    public string SourceDigest { get; }

    public string ViewDigest { get; }

    internal JsonElement ToSnapshotExtension()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("inputMessageCount", InputMessageCount);
            writer.WriteNumber("outputMessageCount", OutputMessageCount);
            writer.WriteNumber("droppedMessageCount", DroppedMessageCount);
            writer.WriteNumber("inputUtf8Bytes", InputUtf8Bytes);
            writer.WriteNumber("outputUtf8Bytes", OutputUtf8Bytes);
            writer.WriteBoolean("compacted", Compacted);
            writer.WriteBoolean("compactionFailed", CompactionFailed);
            writer.WriteBoolean(
                "compactionSkippedByCooldown",
                CompactionSkippedByCooldown);
            writer.WriteString("sourceDigest", SourceDigest);
            writer.WriteString("viewDigest", ViewDigest);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

public sealed partial class ConversationContextView
{
    private const int MaximumPublicMessages = 16_384;
    private const int MaximumPublicUtf8Bytes = 64 * 1_048_576;

    public ConversationContextView(
        IReadOnlyList<NormalizedMessage> messages,
        ConversationContextReport report)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        Report = report ?? throw new ArgumentNullException(nameof(report));
        var input = RuntimeInputGuard.CopyBounded(
            messages,
            MaximumPublicMessages,
            message => message
                       ?? throw new ArgumentException(
                           "Conversation context views cannot contain null messages.",
                           nameof(messages)),
            nameof(messages),
            "conversation_view_messages_exceeded");
        _ = RuntimePromptBuilder.MeasurePrompt(
            input,
            Array.Empty<GameAgent.Protocol.ToolDescriptor>(),
            MaximumPublicMessages,
            MaximumPublicUtf8Bytes,
            estimatedBytesPerToken: 4);
        Messages = new ReadOnlyCollection<NormalizedMessage>(
            RuntimeInputGuard.CopyBounded(
                input,
                MaximumPublicMessages,
                message => NormalizedMessageJournalCodec
                    .CloneValidated(message),
                nameof(messages),
                "conversation_view_messages_exceeded"));
    }

    public IReadOnlyList<NormalizedMessage> Messages { get; }

    public ConversationContextReport Report { get; }
}

public sealed partial class ConversationContextManager :
    IConversationContextEngine
{
    private const int MaximumCooldownEntries = 4_096;
    private const int CooldownMaintenanceInterval = 256;

    private readonly ConversationContextOptions _options;
    private readonly IConversationCompactor _compactor;
    private readonly IRuntimeClock _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset>
        _compactionCooldowns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _detachedCompactions =
        new();
    private readonly SemaphoreSlim _compactionSlots;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _cooldownGate = new();
    private readonly object _lifecycleGate = new();
    private readonly BoundedCancellationDispatcher _shutdownDispatcher;
    private readonly Func<Task>? _detachedCompactionCleanupCheckpoint;
    private readonly RuntimeMetricsEmitter? _metrics;
    private int _cooldownMaintenanceCount;
    private long _nextDetachedCompactionId;
    private int _stopped;
    private int _activePreparations;
    private TaskCompletionSource<bool>? _preparationsDrained;
    private Task? _shutdownCancellationTask;
    private Task? _cleanupTask;
    private int _resourcesDisposed;

    public ConversationContextManager(
        ConversationContextOptions options,
        IConversationCompactor compactor,
        IRuntimeClock clock)
        : this(
            options,
            compactor,
            clock,
            BoundedCancellationDispatcher.LifecycleShared,
            detachedCompactionCleanupCheckpoint: null,
            metrics: null)
    {
    }

    public string EngineId => "bounded-conversation-context";

    public string Version => "1";

    internal ConversationContextManager(
        ConversationContextOptions options,
        IConversationCompactor compactor,
        IRuntimeClock clock,
        BoundedCancellationDispatcher shutdownDispatcher,
        Func<Task>? detachedCompactionCleanupCheckpoint = null,
        RuntimeMetricsEmitter? metrics = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options)))
            .Snapshot();
        _compactor =
            compactor ?? throw new ArgumentNullException(nameof(compactor));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _shutdownDispatcher = shutdownDispatcher
                              ?? throw new ArgumentNullException(
                                  nameof(shutdownDispatcher));
        _detachedCompactionCleanupCheckpoint =
            detachedCompactionCleanupCheckpoint;
        _metrics = metrics;
        _compactionSlots = new SemaphoreSlim(
            _options.MaxConcurrentCompactions,
            _options.MaxConcurrentCompactions);
    }

    public async ValueTask<ConversationContextView> PrepareAsync(
        string runId,
        string turnId,
        IReadOnlyList<NormalizedMessage> transcript,
        IReadOnlyCollection<string>? stablePrefixMessageIds = null,
        CancellationToken cancellationToken = default)
    {
        EnterPreparation();
        try
        {
            return await PrepareCoreAsync(
                    runId,
                    turnId,
                    transcript,
                    stablePrefixMessageIds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitPreparation();
        }
    }

    private async ValueTask<ConversationContextView> PrepareCoreAsync(
        string runId,
        string turnId,
        IReadOnlyList<NormalizedMessage> transcript,
        IReadOnlyCollection<string>? stablePrefixMessageIds,
        CancellationToken cancellationToken)
    {
        RuntimeGuard.RequiredUtf8(runId, 128, nameof(runId));
        RuntimeGuard.RequiredUtf8(turnId, 128, nameof(turnId));
        if (transcript is null)
        {
            throw new ArgumentNullException(nameof(transcript));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var prepared = SnapshotAndMeasure(
            transcript,
            _options,
            cancellationToken);
        var messages = prepared.Messages;
        var stableIdSnapshot = SnapshotStablePrefixMessageIds(
            stablePrefixMessageIds,
            messages,
            _options);
        var stableIds = new HashSet<string>(
            stableIdSnapshot,
            StringComparer.Ordinal);
        var sourceDigest = Digest(prepared.SerializedMessages);
        var inputBytes = prepared.TotalUtf8Bytes;
        if (TryRestoreRegisteredCheckpoint(
                runId,
                sourceDigest,
                messages,
                stableIds,
                inputBytes,
                out var restored))
        {
            return restored;
        }

        if (!_options.Enabled
            || (messages.Count <= _options.MaxRequestMessages
                && inputBytes <= _options.MaxRequestUtf8Bytes))
        {
            return View(
                messages,
                messages.Count,
                inputBytes,
                compacted: false,
                compactionFailed: false,
                compactionSkippedByCooldown: false,
                sourceDigest);
        }

        var atomicGroups = BuildAtomicGroups(messages);
        var requiredIndexes = RequiredIndexes(
            messages,
            stableIds,
            atomicGroups);
        var retainedIndexes = SelectRetainedIndexes(
            messages,
            prepared.MessageUtf8Bytes,
            atomicGroups,
            requiredIndexes,
            reserveSummary: true);
        var droppedIndexes = Enumerable.Range(0, messages.Count)
            .Where(index => !retainedIndexes.Contains(index))
            .ToArray();
        var dropped = droppedIndexes
            .Select(index => messages[index])
            .ToArray();
        var compactionSource = SnapshotWithoutReasoning(dropped);

        var now = _clock.UtcNow;
        MaintainCooldowns(now);
        var skippedByCooldown = false;
        if (_compactionCooldowns.TryGetValue(runId, out var retryAt))
        {
            if (retryAt > now)
            {
                skippedByCooldown = true;
            }
            else
            {
                RemoveCooldown(runId, retryAt);
            }
        }
        NormalizedMessage? summary = null;
        var compactionFailed = false;
        if (compactionSource.Count > 0 && !skippedByCooldown)
        {
            var compactionStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            var compactionOutcome = RuntimeMetricOutcomes.Failure;
            try
            {
                summary = await CompactWithDeadlineAsync(
                        new ConversationCompactionRequest(
                            runId,
                            turnId,
                            compactionSource,
                            Digest(compactionSource),
                            _options.MaxSummaryUtf8Bytes,
                            _options,
                            messagesAreAdmittedSnapshots: true),
                        cancellationToken)
                    .ConfigureAwait(false);
                _compactionCooldowns.TryRemove(runId, out _);
                compactionOutcome = RuntimeMetricOutcomes.Success;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                compactionOutcome = RuntimeMetricOutcomes.Canceled;
                throw;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                compactionFailed = true;
                compactionOutcome = RuntimeMetricOutcomes.Timeout;
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException
                      and not OutOfMemoryException
                      and not StackOverflowException)
            {
                compactionFailed = true;
            }
            finally
            {
                _metrics?.Record(
                    RuntimeMetricNames.CompactionDurationMilliseconds,
                    RuntimeMetricKind.Histogram,
                    RuntimeMetricsEmitter.ElapsedMilliseconds(
                        compactionStarted),
                    outcome: compactionOutcome);
            }

            if (compactionFailed && _options.FailureCooldown > TimeSpan.Zero)
            {
                RecordCooldown(
                    runId,
                    _clock.UtcNow + _options.FailureCooldown);
            }
            else if (summary is not null)
            {
                _metrics?.Record(
                    RuntimeMetricNames.CompactionReclaimedMessages,
                    RuntimeMetricKind.Histogram,
                    Math.Max(0, compactionSource.Count - 1),
                    outcome: RuntimeMetricOutcomes.Success);
            }
        }

        var selected = BuildView(
            messages,
            retainedIndexes,
            summary,
            stableIds);
        selected = FitFinalBudget(
            selected,
            requiredIndexes
                .Select(index => messages[index].MessageId)
                .Concat(
                    summary is null
                        ? Array.Empty<string>()
                        : new[] { summary.MessageId })
                .ToHashSet(StringComparer.Ordinal));

        return View(
            selected,
            messages.Count,
            inputBytes,
            compacted: summary is not null,
            compactionFailed,
            skippedByCooldown,
            sourceDigest);
    }

    internal int CooldownEntryCount => _compactionCooldowns.Count;

    internal int DetachedCompactionCount => _detachedCompactions.Count;

    public bool CleanupCompleted =>
        Volatile.Read(ref _resourcesDisposed) != 0;

    public async ValueTask<bool> StopAsync()
    {
        Task cleanup;
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cleanupTask is null)
            {
                if (!_shutdownDispatcher.TryReserve(out var reservation))
                {
                    return false;
                }

                Task preparationsIdle;
                Task cancellationTask;
                try
                {
                    lock (_lifecycleGate)
                    {
                        _stopped = 1;
                        preparationsIdle = _activePreparations == 0
                            ? Task.CompletedTask
                            : (_preparationsDrained ??=
                                new TaskCompletionSource<bool>(
                                    TaskCreationOptions
                                        .RunContinuationsAsynchronously))
                            .Task;
                        try
                        {
                            cancellationTask =
                                reservation!.DispatchAsync(_shutdown);
                            ClearRegisteredCheckpoints();
                        }
                        catch
                        {
                            _stopped = 0;
                            _preparationsDrained = null;
                            throw;
                        }
                    }
                }
                catch
                {
                    reservation!.Dispose();
                    throw;
                }

                _shutdownCancellationTask = cancellationTask;
                _ = cancellationTask.ContinueWith(
                    _ => reservation!.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _cleanupTask = CompleteCleanupAsync(
                    preparationsIdle,
                    cancellationTask);
            }

            cleanup = _cleanupTask;
        }
        finally
        {
            _stopGate.Release();
        }

        if (!await SettlesWithinAsync(
                cleanup,
                _options.DetachedShutdownTimeout)
                .ConfigureAwait(false))
        {
            return false;
        }

        await cleanup.ConfigureAwait(false);
        return true;
    }

    private async Task CompleteCleanupAsync(
        Task preparationsIdle,
        Task cancellationTask)
    {
        await ObserveAndContinueAsync(
                Task.WhenAll(preparationsIdle, cancellationTask))
            .ConfigureAwait(false);
        while (true)
        {
            var detached = _detachedCompactions.Values.ToArray();
            if (detached.Length == 0)
            {
                break;
            }

            await ObserveAndContinueAsync(Task.WhenAll(detached))
                .ConfigureAwait(false);
        }

        _compactionCooldowns.Clear();
        ClearRegisteredCheckpoints();
        _compactionSlots.Dispose();
        _shutdown.Dispose();
        Volatile.Write(ref _resourcesDisposed, 1);
    }

    private static async Task ObserveAndContinueAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            ObserveCompletion(operation);
        }
    }

    private void EnterPreparation()
    {
        lock (_lifecycleGate)
        {
            if (_stopped != 0)
            {
                throw new ObjectDisposedException(
                    nameof(ConversationContextManager));
            }

            _activePreparations = checked(_activePreparations + 1);
        }
    }

    private void ExitPreparation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_lifecycleGate)
        {
            _activePreparations--;
            if (_activePreparations < 0)
            {
                throw new InvalidOperationException(
                    "The active conversation preparation count became invalid.");
            }

            if (_stopped != 0 && _activePreparations == 0)
            {
                drained = _preparationsDrained;
            }
        }

        drained?.TrySetResult(true);
    }

    private static async Task<bool> SettlesWithinAsync(
        Task operation,
        TimeSpan timeout)
    {
        var completed = await Task.WhenAny(operation, Task.Delay(timeout))
            .ConfigureAwait(false);
        return ReferenceEquals(completed, operation);
    }

    private void RecordCooldown(
        string runId,
        DateTimeOffset retryAt)
    {
        lock (_cooldownGate)
        {
            if (_compactionCooldowns.Count >= MaximumCooldownEntries
                && !_compactionCooldowns.ContainsKey(runId))
            {
                EvictOneCooldown(_clock.UtcNow);
            }

            _compactionCooldowns[runId] = retryAt;
        }
    }

    private void MaintainCooldowns(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _cooldownMaintenanceCount)
            % CooldownMaintenanceInterval != 0)
        {
            return;
        }

        foreach (var pair in _compactionCooldowns)
        {
            if (pair.Value <= now)
            {
                RemoveCooldown(pair.Key, pair.Value);
            }
        }
    }

    private void EvictOneCooldown(DateTimeOffset now)
    {
        var candidate = _compactionCooldowns
            .OrderBy(
                pair => pair.Value > now
                    ? 1
                    : 0)
            .ThenBy(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(candidate.Key))
        {
            RemoveCooldown(candidate.Key, candidate.Value);
        }
    }

    private void RemoveCooldown(
        string runId,
        DateTimeOffset retryAt)
    {
        _ = ((ICollection<KeyValuePair<string, DateTimeOffset>>)
                _compactionCooldowns)
            .Remove(new KeyValuePair<string, DateTimeOffset>(
                runId,
                retryAt));
    }

    private async ValueTask<NormalizedMessage> CompactWithDeadlineAsync(
        ConversationCompactionRequest request,
        CancellationToken cancellationToken)
    {
        var absoluteDeadline =
            MonotonicDeadline.Start(_options.CompactionTimeout);
        using var waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        deadline.CancelAfter(absoluteDeadline.Remaining);
        try
        {
            await _compactionSlots.WaitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            deadline.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            _shutdown.Token.ThrowIfCancellationRequested();
            throw new TimeoutException(
                "Conversation compaction exceeded its deadline.");
        }
        catch
        {
            deadline.Dispose();
            throw;
        }

        if (absoluteDeadline.Remaining <= TimeSpan.Zero)
        {
            deadline.Dispose();
            _compactionSlots.Release();
            throw new TimeoutException(
                "Conversation compaction exceeded its deadline.");
        }

        Task<ConversationCompactionResult> operation;
        try
        {
            operation = Task.Run(
                async () => await _compactor
                    .CompactAsync(request, deadline.Token)
                    .ConfigureAwait(false));
        }
        catch
        {
            deadline.Dispose();
            _compactionSlots.Release();
            throw;
        }

        var remaining = absoluteDeadline.Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            TrackDetachedCompaction(operation, deadline);
            throw new TimeoutException(
                "Conversation compaction exceeded its deadline.");
        }

        var timeout = Task.Delay(
            remaining,
            waitCancellation.Token);
        var completed = await Task.WhenAny(operation, timeout)
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, operation))
        {
            TrackDetachedCompaction(operation, deadline);
            cancellationToken.ThrowIfCancellationRequested();
            _shutdown.Token.ThrowIfCancellationRequested();
            throw new TimeoutException(
                "Conversation compaction exceeded its deadline.");
        }

        try
        {
            ConversationCompactionResult? result = null;
            Exception? primaryFailure = null;
            try
            {
                result = await operation.ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException
                      and not OutOfMemoryException
                      and not StackOverflowException)
            {
                primaryFailure = exception;
            }

            deadline.Token.ThrowIfCancellationRequested();
            var analysis = result is not null
                           && string.Equals(
                               result.SourceDigest,
                               request.SourceDigest,
                               StringComparison.Ordinal)
                ? result.QualityAnalysis
                : null;
            analysis ??= ConversationSummaryQuality.Analyze(
                request,
                deadline.Token);
            if (result is not null
                && ConversationSummaryQuality.TryCreateAdmittedSummary(
                    request,
                    result,
                    analysis,
                    deadline.Token,
                    out var admitted,
                    out _))
            {
                return admitted!;
            }

            if (_compactor is ExtractiveConversationCompactor)
            {
                throw primaryFailure
                      ?? new InvalidDataException(
                          "The deterministic conversation summary failed "
                          + "its admission checks.");
            }

            var fallback =
                ConversationSummaryQuality.CreateDeterministicResult(
                    request,
                    analysis,
                    deadline.Token);
            if (!ConversationSummaryQuality.TryCreateAdmittedSummary(
                    request,
                    fallback,
                    analysis,
                    deadline.Token,
                    out admitted,
                    out var fallbackRejection))
            {
                throw new InvalidDataException(
                    "The deterministic conversation summary fallback was "
                    + $"rejected: {fallbackRejection}.");
            }

            return admitted!;
        }
        finally
        {
            deadline.Dispose();
            _compactionSlots.Release();
        }
    }

    private void TrackDetachedCompaction(
        Task operation,
        CancellationTokenSource deadline)
    {
        long id;
        TaskCompletionSource<bool> start;
        Task cleanup;
        do
        {
            id = Interlocked.Increment(ref _nextDetachedCompactionId);
            start = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup = CompleteDetachedCompactionAsync(
                id,
                operation,
                deadline,
                start.Task);
        }
        while (!_detachedCompactions.TryAdd(id, cleanup));

        start.TrySetResult(true);
        _ = cleanup.ContinueWith(
            ObserveCompletion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task CompleteDetachedCompactionAsync(
        long id,
        Task operation,
        CancellationTokenSource deadline,
        Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch
            {
                ObserveCompletion(operation);
            }

            if (_detachedCompactionCleanupCheckpoint is not null)
            {
                await _detachedCompactionCleanupCheckpoint()
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                deadline.Dispose();
                _compactionSlots.Release();
            }
            finally
            {
                _detachedCompactions.TryRemove(id, out _);
            }
        }
    }

    private static void ObserveCompletion(Task operation)
    {
        if (operation.IsFaulted)
        {
            _ = operation.Exception;
        }
    }

    private HashSet<int> SelectRetainedIndexes(
        IReadOnlyList<NormalizedMessage> messages,
        IReadOnlyList<int> messageUtf8Bytes,
        IReadOnlyList<IReadOnlyList<int>> atomicGroups,
        HashSet<int> required,
        bool reserveSummary)
    {
        var selected = new HashSet<int>(required);
        var maximumMessages = _options.MaxRequestMessages
                              - (reserveSummary ? 1 : 0);
        var maximumBytes = _options.MaxRequestUtf8Bytes
                           - (reserveSummary
                               ? _options.MaxSummaryUtf8Bytes
                               : 0);
        var selectedBytes = required.Sum(index => messageUtf8Bytes[index]);
        if (selected.Count > maximumMessages || selectedBytes > maximumBytes)
        {
            throw new RuntimeContentLimitException(
                nameof(messages),
                "conversation_required_context_exceeds_budget",
                "Required conversation messages exceed the request budget.");
        }

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (selected.Contains(index))
            {
                continue;
            }

            var group = atomicGroups[index];
            var additions = group.Where(item => !selected.Contains(item)).ToArray();
            var addedBytes = additions.Sum(item => messageUtf8Bytes[item]);
            if (selected.Count + additions.Length > maximumMessages
                || selectedBytes + addedBytes > maximumBytes)
            {
                continue;
            }

            foreach (var addition in additions)
            {
                selected.Add(addition);
            }
            selectedBytes += addedBytes;
        }

        return selected;
    }

    private HashSet<int> RequiredIndexes(
        IReadOnlyList<NormalizedMessage> messages,
        HashSet<string> stableIds,
        IReadOnlyList<IReadOnlyList<int>> atomicGroups)
    {
        var required = new HashSet<int>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (stableIds.Contains(messages[index].MessageId)
                || string.Equals(
                    messages[index].Role,
                    NormalizedRoles.System,
                    StringComparison.Ordinal))
            {
                required.Add(index);
            }
        }

        var latestUser = -1;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (string.Equals(
                    messages[index].Role,
                    NormalizedRoles.User,
                    StringComparison.Ordinal))
            {
                latestUser = index;
                break;
            }
        }
        if (latestUser >= 0)
        {
            required.Add(latestUser);
        }

        var recentStart = Math.Max(
            0,
            messages.Count - _options.RecentMessagesToKeep);
        for (var index = recentStart; index < messages.Count; index++)
        {
            foreach (var groupedIndex in atomicGroups[index])
            {
                required.Add(groupedIndex);
            }
        }

        var completedCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var part in message.Parts.Where(
                         part => string.Equals(
                             part.Type,
                             NormalizedPartTypes.ToolResult,
                             StringComparison.Ordinal)))
            {
                if (!string.IsNullOrEmpty(part.ToolCallId))
                {
                    completedCalls.Add(part.ToolCallId);
                }
            }
        }

        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].Parts.Any(
                    part => string.Equals(
                                part.Type,
                                NormalizedPartTypes.ToolCall,
                                StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(part.ToolCallId)
                            && !completedCalls.Contains(part.ToolCallId)))
            {
                required.Add(index);
            }
        }

        foreach (var index in required.ToArray())
        {
            foreach (var groupedIndex in atomicGroups[index])
            {
                required.Add(groupedIndex);
            }
        }

        return required;
    }

    private static IReadOnlyList<IReadOnlyList<int>> BuildAtomicGroups(
        IReadOnlyList<NormalizedMessage> messages)
    {
        var callIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var resultIndexes = new Dictionary<string, List<int>>(
            StringComparer.Ordinal);
        for (var index = 0; index < messages.Count; index++)
        {
            foreach (var part in messages[index].Parts)
            {
                if (string.IsNullOrEmpty(part.ToolCallId))
                {
                    continue;
                }

                if (string.Equals(
                        part.Type,
                        NormalizedPartTypes.ToolCall,
                        StringComparison.Ordinal))
                {
                    callIndexes.TryAdd(part.ToolCallId, index);
                }
                else if (string.Equals(
                             part.Type,
                             NormalizedPartTypes.ToolResult,
                             StringComparison.Ordinal))
                {
                    if (!resultIndexes.TryGetValue(
                            part.ToolCallId,
                            out var indexes))
                    {
                        indexes = new List<int>();
                        resultIndexes.Add(part.ToolCallId, indexes);
                    }

                    indexes.Add(index);
                }
            }
        }

        var parents = Enumerable.Range(0, messages.Count).ToArray();
        foreach (var pair in callIndexes)
        {
            if (!resultIndexes.TryGetValue(pair.Key, out var results))
            {
                continue;
            }

            foreach (var result in results)
            {
                Union(pair.Value, result);
            }
        }

        var components = Enumerable.Range(0, messages.Count)
            .GroupBy(Find)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<int>)group.OrderBy(index => index)
                    .ToArray());
        return Enumerable.Range(0, messages.Count)
            .Select(index => components[Find(index)])
            .ToArray();

        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                parents[rightRoot] = leftRoot;
            }
        }
    }

    private IReadOnlyList<int> AtomicGroup(
        IReadOnlyList<NormalizedMessage> messages,
        int index)
    {
        var message = messages[index];
        var callIds = message.Parts
            .Where(
                part => string.Equals(
                    part.Type,
                    NormalizedPartTypes.ToolCall,
                    StringComparison.Ordinal))
            .Select(part => part.ToolCallId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (callIds.Count > 0)
        {
            var group = new List<int> { index };
            for (var next = index + 1; next < messages.Count; next++)
            {
                var resultIds = messages[next].Parts
                    .Where(
                        part => string.Equals(
                            part.Type,
                            NormalizedPartTypes.ToolResult,
                            StringComparison.Ordinal))
                    .Select(part => part.ToolCallId);
                if (resultIds.Any(
                        id => id is not null && callIds.Contains(id)))
                {
                    group.Add(next);
                }
            }
            return group;
        }

        var resultCallIds = message.Parts
            .Where(
                part => string.Equals(
                    part.Type,
                    NormalizedPartTypes.ToolResult,
                    StringComparison.Ordinal))
            .Select(part => part.ToolCallId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (resultCallIds.Count == 0)
        {
            return new[] { index };
        }

        for (var previous = index - 1; previous >= 0; previous--)
        {
            if (messages[previous].Parts.Any(
                    part => string.Equals(
                                part.Type,
                                NormalizedPartTypes.ToolCall,
                                StringComparison.Ordinal)
                            && part.ToolCallId is not null
                            && resultCallIds.Contains(part.ToolCallId)))
            {
                return AtomicGroup(messages, previous);
            }
        }

        return new[] { index };
    }

    private static List<NormalizedMessage> BuildView(
        IReadOnlyList<NormalizedMessage> messages,
        HashSet<int> retained,
        NormalizedMessage? summary,
        HashSet<string> stableIds)
    {
        var result = new List<NormalizedMessage>();
        var summaryAdded = false;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (retained.Contains(index))
            {
                if (!summaryAdded
                    && summary is not null
                    && !stableIds.Contains(message.MessageId))
                {
                    result.Add(summary);
                    summaryAdded = true;
                }
                result.Add(message);
            }
        }

        if (!summaryAdded && summary is not null)
        {
            result.Add(summary);
        }

        return result;
    }

    private List<NormalizedMessage> FitFinalBudget(
        List<NormalizedMessage> messages,
        HashSet<string> requiredMessageIds)
    {
        while (messages.Count > _options.MaxRequestMessages
               || Measure(messages) > _options.MaxRequestUtf8Bytes)
        {
            var removable = messages.FindIndex(
                message => !requiredMessageIds.Contains(message.MessageId));
            if (removable < 0)
            {
                throw new RuntimeContentLimitException(
                    nameof(messages),
                    "conversation_required_context_exceeds_budget",
                    "Required conversation messages exceed the request budget.");
            }

            var group = AtomicGroup(messages, removable)
                .OrderByDescending(index => index)
                .ToArray();
            if (group.Any(
                    index => requiredMessageIds.Contains(
                        messages[index].MessageId)))
            {
                requiredMessageIds.Add(messages[removable].MessageId);
                continue;
            }
            foreach (var index in group)
            {
                messages.RemoveAt(index);
            }
        }

        return messages;
    }

    private static ConversationContextView View(
        IReadOnlyList<NormalizedMessage> messages,
        int inputMessageCount,
        int inputBytes,
        bool compacted,
        bool compactionFailed,
        bool compactionSkippedByCooldown,
        string sourceDigest)
    {
        var outputBytes = Measure(messages);
        return new ConversationContextView(
            messages,
            new ConversationContextReport(
                inputMessageCount,
                messages.Count,
                inputMessageCount - messages.Count + (compacted ? 1 : 0),
                inputBytes,
                outputBytes,
                compacted,
                compactionFailed,
                compactionSkippedByCooldown,
                sourceDigest,
                Digest(messages)));
    }

    private static List<NormalizedMessage> Snapshot(
        IReadOnlyList<NormalizedMessage> messages)
    {
        var result = new List<NormalizedMessage>(messages.Count);
        foreach (var message in messages)
        {
            result.Add(NormalizedMessageJournalCodec.CloneValidated(message));
        }
        return result;
    }

    private static IReadOnlyList<NormalizedMessage> SnapshotWithoutReasoning(
        IReadOnlyList<NormalizedMessage> messages)
    {
        var result = new List<NormalizedMessage>(messages.Count);
        foreach (var source in messages)
        {
            if (!source.Parts.Any(
                    part => string.Equals(
                        part.Type,
                        NormalizedPartTypes.Reasoning,
                        StringComparison.Ordinal)))
            {
                result.Add(source);
                continue;
            }

            var snapshot = NormalizedMessageJournalCodec.CloneValidated(source);
            snapshot.Parts = snapshot.Parts
                .Where(
                    part => !string.Equals(
                        part.Type,
                        NormalizedPartTypes.Reasoning,
                        StringComparison.Ordinal))
                .ToList();
            if (snapshot.Parts.Count > 0)
            {
                result.Add(snapshot);
            }
        }

        return new ReadOnlyCollection<NormalizedMessage>(result);
    }

    private static PreparedConversation SnapshotAndMeasure(
        IReadOnlyList<NormalizedMessage> messages,
        ConversationContextOptions options,
        CancellationToken cancellationToken)
    {
        var input = SnapshotIndexed(
            messages,
            options.MaxInputMessages,
            nameof(messages),
            "conversation_input_messages_exceeded");
        var snapshots = new List<NormalizedMessage>(input.Length);
        var serialized = new string[input.Length];
        var sizes = new int[input.Length];
        var totalUtf8Bytes = 0;
        var preflightUtf8Bytes = 0;
        var totalJsonNodes = 0;
        for (var index = 0; index < input.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shape = SnapshotMessageShape(
                input[index],
                options,
                ref preflightUtf8Bytes,
                ref totalJsonNodes);
            var remainingBytes =
                options.MaxInputUtf8Bytes - totalUtf8Bytes;
            if (remainingBytes <= 0)
            {
                throw InputUtf8Limit(options.MaxInputUtf8Bytes);
            }

            var measuredBytes = MeasureInputMessageBounded(
                shape,
                remainingBytes,
                options.MaxInputUtf8Bytes);
            totalUtf8Bytes = checked(totalUtf8Bytes + measuredBytes);

            var snapshot = NormalizedMessageJournalCodec.CloneValidated(
                shape.ToMessage(),
                cancellationToken);
            var encodedText =
                NormalizedMessageJournalCodec.EncodeText(snapshot);
            var encodedBytes = Encoding.UTF8.GetByteCount(encodedText);
            if (encodedBytes != measuredBytes)
            {
                throw new InvalidOperationException(
                    "Conversation input measurement diverged from encoding.");
            }

            serialized[index] = encodedText;
            sizes[index] = encodedBytes;
            snapshots.Add(snapshot);
        }

        return new PreparedConversation(
            snapshots,
            serialized,
            sizes,
            totalUtf8Bytes);
    }

    internal static IReadOnlyList<NormalizedMessage>
        SnapshotCompactionMessages(
            IReadOnlyList<NormalizedMessage> messages,
            ConversationContextOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var prepared = SnapshotAndMeasure(
            messages,
            options,
            CancellationToken.None);
        return new ReadOnlyCollection<NormalizedMessage>(
            prepared.Messages.ToArray());
    }

    internal static string[] SnapshotStablePrefixMessageIds(
        IReadOnlyCollection<string>? stablePrefixMessageIds,
        IReadOnlyList<NormalizedMessage> transcript,
        ConversationContextOptions options)
    {
        if (stablePrefixMessageIds is null)
        {
            return Array.Empty<string>();
        }
        if (stablePrefixMessageIds is not IReadOnlyList<string> indexedIds)
        {
            throw new ArgumentException(
                "Stable message identifiers must provide bounded indexed "
                + "access through IReadOnlyList<string>.",
                nameof(stablePrefixMessageIds));
        }

        var input = SnapshotIndexed(
            indexedIds,
            options.MaxStablePrefixMessageIds,
            nameof(stablePrefixMessageIds),
            "conversation_stable_prefix_items_exceeded");
        var transcriptIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < transcript.Count; index++)
        {
            transcriptIds.Add(transcript[index].MessageId);
        }

        var result = new string[input.Length];
        var totalUtf8Bytes = 0;
        for (var index = 0; index < input.Length; index++)
        {
            var id = RuntimeGuard.RequiredUtf8(
                input[index],
                128,
                nameof(stablePrefixMessageIds));
            var utf8Bytes = Encoding.UTF8.GetByteCount(id);
            if (utf8Bytes
                > options.MaxStablePrefixUtf8Bytes - totalUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(stablePrefixMessageIds),
                    "conversation_stable_prefix_bytes_exceeded",
                    "Stable conversation identifiers exceed "
                    + $"{options.MaxStablePrefixUtf8Bytes} UTF-8 bytes.");
            }

            if (!transcriptIds.Contains(id))
            {
                throw new ArgumentException(
                    $"Stable message identifier '{id}' does not reference "
                    + "the admitted transcript snapshot.",
                    nameof(stablePrefixMessageIds));
            }

            totalUtf8Bytes += utf8Bytes;
            result[index] = id;
        }

        return result;
    }

    private static InputMessageSnapshot SnapshotMessageShape(
        NormalizedMessage? message,
        ConversationContextOptions options,
        ref int preflightUtf8Bytes,
        ref int totalJsonNodes)
    {
        if (message is null)
        {
            throw new ArgumentException(
                "Conversation transcripts cannot contain null messages.",
                "messages");
        }

        var messageId = message.MessageId;
        var role = message.Role;
        var createdAt = message.CreatedAt;
        ChargeInputUtf8(
            ref preflightUtf8Bytes,
            messageId,
            options);
        ChargeInputUtf8(
            ref preflightUtf8Bytes,
            role,
            options);
        var sourceParts = message.Parts;
        if (sourceParts is null)
        {
            throw new InvalidDataException(
                "A normalized message checkpoint is incomplete.");
        }

        ChargeInputNodes(ref totalJsonNodes, 5, options);
        var remainingNodes = options.MaxInputJsonNodes - totalJsonNodes;
        var parts = SnapshotIndexed(
            sourceParts,
            Math.Max(0, remainingNodes),
            "messages",
            "conversation_input_json_nodes_exceeded");
        if (parts.Length == 0)
        {
            throw new InvalidDataException(
                "A normalized message checkpoint cannot be empty.");
        }

        var partSnapshots = new InputPartSnapshot[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part is null)
            {
                throw new InvalidDataException(
                    "A normalized message checkpoint contains a null part.");
            }

            var type = part.Type;
            var text = part.Text;
            var json = part.Json;
            var toolCallId = part.ToolCallId;
            var toolName = part.ToolName;
            var toolVersion = part.ToolVersion;
            var toolEffect = part.ToolEffect;
            var toolDescriptorDigest = part.ToolDescriptorDigest;

            ChargeInputUtf8(
                ref preflightUtf8Bytes,
                type,
                options);
            ChargeInputUtf8(
                ref preflightUtf8Bytes,
                text,
                options);
            ChargeInputUtf8(
                ref preflightUtf8Bytes,
                toolCallId,
                options);
            ChargeInputUtf8(
                ref preflightUtf8Bytes,
                toolName,
                options);
            ChargeInputUtf8(
                ref preflightUtf8Bytes,
                toolVersion,
                options);
            ChargeInputUtf8(
                ref preflightUtf8Bytes,
                toolEffect,
                options);
            ChargeInputUtf8(
                ref preflightUtf8Bytes,
                toolDescriptorDigest,
                options);

            ChargeInputNodes(ref totalJsonNodes, 2, options);
            if (text is not null)
            {
                ChargeInputNodes(ref totalJsonNodes, 1, options);
            }
            if (toolCallId is not null)
            {
                ChargeInputNodes(ref totalJsonNodes, 1, options);
            }
            if (toolName is not null)
            {
                ChargeInputNodes(ref totalJsonNodes, 1, options);
            }
            if (toolVersion is not null)
            {
                ChargeInputNodes(ref totalJsonNodes, 1, options);
            }
            if (toolEffect is not null)
            {
                ChargeInputNodes(ref totalJsonNodes, 1, options);
            }
            if (toolDescriptorDigest is not null)
            {
                ChargeInputNodes(ref totalJsonNodes, 1, options);
            }

            if (json.HasValue)
            {
                var remainingJsonNodes =
                    options.MaxInputJsonNodes - totalJsonNodes;
                if (remainingJsonNodes <= 0)
                {
                    throw InputJsonNodeLimit(
                        options.MaxInputJsonNodes);
                }

                JsonValueMeasurement measurement;
                try
                {
                    var remainingInputUtf8Bytes =
                        options.MaxInputUtf8Bytes
                        - preflightUtf8Bytes;
                    if (remainingInputUtf8Bytes <= 0)
                    {
                        throw InputUtf8Limit(
                            options.MaxInputUtf8Bytes);
                    }

                    measurement =
                        JsonValueInspector.ValidateAndMeasureDetailed(
                            json.Value,
                            new JsonValueLimits(
                                maxUtf8Bytes:
                                remainingInputUtf8Bytes,
                                maxDepth: 64,
                                maxNodes: remainingJsonNodes,
                                maxStringUtf8Bytes:
                                options.MaxInputUtf8Bytes,
                                maxContainerItems:
                                remainingJsonNodes),
                            "messages");
                }
                catch (RuntimeContentLimitException exception)
                    when (exception.LimitCode is
                          "json_nodes_exceeded" or
                          "json_container_items_exceeded")
                {
                    throw InputJsonNodeLimit(
                        options.MaxInputJsonNodes);
                }
                catch (RuntimeContentLimitException exception)
                    when (exception.LimitCode is
                          "json_bytes_exceeded" or
                          "json_string_bytes_exceeded")
                {
                    throw InputUtf8Limit(options.MaxInputUtf8Bytes);
                }

                ChargeInputNodes(
                    ref totalJsonNodes,
                    measurement.Nodes,
                    options);
                ChargeInputUtf8(
                    ref preflightUtf8Bytes,
                    measurement.Utf8Bytes,
                    options);
            }

            partSnapshots[index] = new InputPartSnapshot(
                type,
                text,
                json,
                toolCallId,
                toolName,
                toolVersion,
                toolEffect,
                toolDescriptorDigest);
        }

        return new InputMessageSnapshot(
            messageId,
            role,
            createdAt,
            partSnapshots);
    }

    private static int MeasureInputMessageBounded(
        InputMessageSnapshot message,
        int maximumUtf8Bytes,
        int configuredMaximumUtf8Bytes)
    {
        using var buffer = new ConversationInputCountingBufferWriter(
            maximumUtf8Bytes,
            configuredMaximumUtf8Bytes);
        try
        {
            using var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions
                {
                    MaxDepth = 66
                });
            writer.WriteStartObject();
            writer.WriteString("messageId", message.MessageId);
            writer.WriteString("role", message.Role);
            writer.WriteString("createdAt", message.CreatedAt);
            writer.WriteStartArray("parts");
            for (var index = 0; index < message.Parts.Length; index++)
            {
                var part = message.Parts[index];
                writer.WriteStartObject();
                writer.WriteString("type", part.Type);
                if (part.Text is not null)
                {
                    writer.WriteString("text", part.Text);
                }
                if (part.Json.HasValue)
                {
                    writer.WritePropertyName("json");
                    part.Json.Value.WriteTo(writer);
                }
                if (part.ToolCallId is not null)
                {
                    writer.WriteString("toolCallId", part.ToolCallId);
                }
                if (part.ToolName is not null)
                {
                    writer.WriteString("toolName", part.ToolName);
                }
                if (part.ToolVersion is not null)
                {
                    writer.WriteString("toolVersion", part.ToolVersion);
                }
                if (part.ToolEffect is not null)
                {
                    writer.WriteString("toolEffect", part.ToolEffect);
                }
                if (part.ToolDescriptorDigest is not null)
                {
                    writer.WriteString(
                        "toolDescriptorDigest",
                        part.ToolDescriptorDigest);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        catch (RuntimeContentLimitException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException
                  or InvalidOperationException)
        {
            throw new InvalidDataException(
                "A normalized message contains invalid JSON.",
                exception);
        }

        return buffer.WrittenBytes;
    }

    private static T[] SnapshotIndexed<T>(
        IReadOnlyList<T> values,
        int maximumItems,
        string parameterName,
        string limitCode)
    {
        if (values is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        int count;
        try
        {
            count = values.Count;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new ArgumentException(
                "The input collection count could not be read.",
                parameterName,
                exception);
        }

        if (count < 0)
        {
            throw new ArgumentException(
                "The input collection returned a negative count.",
                parameterName);
        }
        if (count > maximumItems)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                limitCode,
                $"The input collection exceeds {maximumItems} items.");
        }

        var result = new T[count];
        for (var index = 0; index < count; index++)
        {
            try
            {
                result[index] = values[index];
            }
            catch (Exception exception)
                when (exception is ArgumentOutOfRangeException
                      or IndexOutOfRangeException)
            {
                throw new ArgumentException(
                    "The input collection count and indexed contents "
                    + "are inconsistent.",
                    parameterName,
                    exception);
            }
        }

        return result;
    }

    private static void ChargeInputNodes(
        ref int totalJsonNodes,
        int nodes,
        ConversationContextOptions options)
    {
        if (nodes < 0
            || nodes > options.MaxInputJsonNodes - totalJsonNodes)
        {
            throw InputJsonNodeLimit(options.MaxInputJsonNodes);
        }

        totalJsonNodes += nodes;
    }

    private static void ChargeInputUtf8(
        ref int totalUtf8Bytes,
        string? value,
        ConversationContextOptions options)
    {
        if (value is null)
        {
            return;
        }

        var remaining = options.MaxInputUtf8Bytes - totalUtf8Bytes;
        if (value.Length > remaining)
        {
            throw InputUtf8Limit(options.MaxInputUtf8Bytes);
        }

        var utf8Bytes = Encoding.UTF8.GetByteCount(value);
        ChargeInputUtf8(
            ref totalUtf8Bytes,
            utf8Bytes,
            options);
    }

    private static void ChargeInputUtf8(
        ref int totalUtf8Bytes,
        int utf8Bytes,
        ConversationContextOptions options)
    {
        if (utf8Bytes < 0
            || utf8Bytes
            > options.MaxInputUtf8Bytes - totalUtf8Bytes)
        {
            throw InputUtf8Limit(options.MaxInputUtf8Bytes);
        }

        totalUtf8Bytes += utf8Bytes;
    }

    private static RuntimeContentLimitException InputUtf8Limit(
        int maximumUtf8Bytes)
    {
        return new RuntimeContentLimitException(
            "messages",
            "conversation_input_utf8_bytes_exceeded",
            "Conversation input exceeds "
            + $"{maximumUtf8Bytes} encoded UTF-8 bytes.");
    }

    private static RuntimeContentLimitException InputJsonNodeLimit(
        int maximumJsonNodes)
    {
        return new RuntimeContentLimitException(
            "messages",
            "conversation_input_json_nodes_exceeded",
            "Conversation input exceeds "
            + $"{maximumJsonNodes} JSON nodes.");
    }

    internal static int Measure(IReadOnlyList<NormalizedMessage> messages)
    {
        var bytes = 0;
        foreach (var message in messages)
        {
            bytes = checked(bytes + Measure(message));
        }
        return bytes;
    }

    private static int Measure(NormalizedMessage message)
    {
        return Encoding.UTF8.GetByteCount(
            NormalizedMessageJournalCodec.EncodeText(message));
    }

    internal static string Digest(IReadOnlyList<NormalizedMessage> messages)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "conversation-context");
        digest.Add("count", messages.Count);
        foreach (var message in messages)
        {
            digest.Add(
                "message",
                NormalizedMessageJournalCodec.EncodeText(message));
        }
        return digest.Finish();
    }

    internal static string Digest(IReadOnlyList<string> serializedMessages)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "conversation-context");
        digest.Add("count", serializedMessages.Count);
        foreach (var message in serializedMessages)
        {
            digest.Add("message", message);
        }

        return digest.Finish();
    }

    private sealed class InputMessageSnapshot
    {
        public InputMessageSnapshot(
            string messageId,
            string role,
            DateTimeOffset createdAt,
            InputPartSnapshot[] parts)
        {
            MessageId = messageId;
            Role = role;
            CreatedAt = createdAt;
            Parts = parts;
        }

        public string MessageId { get; }

        public string Role { get; }

        public DateTimeOffset CreatedAt { get; }

        public InputPartSnapshot[] Parts { get; }

        public NormalizedMessage ToMessage()
        {
            var parts = new List<NormalizedContentPart>(Parts.Length);
            for (var index = 0; index < Parts.Length; index++)
            {
                var part = Parts[index];
                parts.Add(
                    new NormalizedContentPart
                    {
                        Type = part.Type,
                        Text = part.Text,
                        Json = part.Json,
                        ToolCallId = part.ToolCallId,
                        ToolName = part.ToolName,
                        ToolVersion = part.ToolVersion,
                        ToolEffect = part.ToolEffect,
                        ToolDescriptorDigest =
                            part.ToolDescriptorDigest
                    });
            }

            return new NormalizedMessage
            {
                MessageId = MessageId,
                Role = Role,
                CreatedAt = CreatedAt,
                Parts = parts
            };
        }
    }

    private sealed class InputPartSnapshot
    {
        public InputPartSnapshot(
            string type,
            string? text,
            JsonElement? json,
            string? toolCallId,
            string? toolName,
            string? toolVersion,
            string? toolEffect,
            string? toolDescriptorDigest)
        {
            Type = type;
            Text = text;
            Json = json;
            ToolCallId = toolCallId;
            ToolName = toolName;
            ToolVersion = toolVersion;
            ToolEffect = toolEffect;
            ToolDescriptorDigest = toolDescriptorDigest;
        }

        public string Type { get; }

        public string? Text { get; }

        public JsonElement? Json { get; }

        public string? ToolCallId { get; }

        public string? ToolName { get; }

        public string? ToolVersion { get; }

        public string? ToolEffect { get; }

        public string? ToolDescriptorDigest { get; }
    }

    private sealed class ConversationInputCountingBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private const int DefaultSizeHint = 256;
        private const int WriterSlackBytes = 4_096;

        private readonly int _maximumBytes;
        private readonly int _maximumBufferBytes;
        private readonly int _configuredMaximumBytes;
        private byte[]? _buffer;
        private int _writtenBytes;

        public ConversationInputCountingBufferWriter(
            int maximumBytes,
            int configuredMaximumBytes)
        {
            if (maximumBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumBytes));
            }
            if (configuredMaximumBytes < maximumBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(configuredMaximumBytes));
            }

            _maximumBytes = maximumBytes;
            _configuredMaximumBytes = configuredMaximumBytes;
            _maximumBufferBytes = (int)Math.Min(
                int.MaxValue,
                (long)maximumBytes + WriterSlackBytes);
        }

        public int WrittenBytes => _writtenBytes;

        public void Advance(int count)
        {
            if (count < 0
                || _buffer is null
                || count > _buffer.Length
                || count > _maximumBytes - _writtenBytes)
            {
                throw InputUtf8Limit(_configuredMaximumBytes);
            }

            _writtenBytes += count;
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
                throw InputUtf8Limit(_configuredMaximumBytes);
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
    }

    private sealed class PreparedConversation
    {
        public PreparedConversation(
            IReadOnlyList<NormalizedMessage> messages,
            IReadOnlyList<string> serializedMessages,
            IReadOnlyList<int> messageUtf8Bytes,
            int totalUtf8Bytes)
        {
            Messages = messages;
            SerializedMessages = serializedMessages;
            MessageUtf8Bytes = messageUtf8Bytes;
            TotalUtf8Bytes = totalUtf8Bytes;
        }

        public IReadOnlyList<NormalizedMessage> Messages { get; }

        public IReadOnlyList<string> SerializedMessages { get; }

        public IReadOnlyList<int> MessageUtf8Bytes { get; }

        public int TotalUtf8Bytes { get; }
    }
}
