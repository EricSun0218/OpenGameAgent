using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public sealed class GameTranscriptCompactionContext
{
    public GameTranscriptCompactionContext(
        GameSessionKey session,
        IReadOnlyList<AgentMessage> messages,
        int targetMessageCount,
        long? targetEstimatedTokens = null,
        GameTranscriptTokenEstimator? tokenEstimator = null,
        long? maximumSummaryUsageTokens = null)
    {
        if (targetMessageCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMessageCount));
        }

        if (targetEstimatedTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetEstimatedTokens));
        }

        if (targetEstimatedTokens is not null && tokenEstimator is null)
        {
            throw new ArgumentException(
                "A token estimator is required when a token target is configured.",
                nameof(tokenEstimator));
        }

        if (maximumSummaryUsageTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSummaryUsageTokens));
        }

        Session = session.EnsureValid(nameof(session));
        var copiedMessages = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
        if (copiedMessages.Any(message => message is null))
        {
            throw new ArgumentException("A transcript cannot contain null messages.", nameof(messages));
        }

        Messages = Array.AsReadOnly(copiedMessages);
        TargetMessageCount = targetMessageCount;
        TargetEstimatedTokens = targetEstimatedTokens;
        TokenEstimator = tokenEstimator;
        MaximumSummaryUsageTokens = maximumSummaryUsageTokens;
    }

    public GameSessionKey Session { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public int TargetMessageCount { get; }

    public long? TargetEstimatedTokens { get; }

    public GameTranscriptTokenEstimator? TokenEstimator { get; }

    public long? MaximumSummaryUsageTokens { get; }
}

public delegate long GameTranscriptTokenEstimator(IReadOnlyList<AgentMessage> messages);

public delegate long GameModelRequestTokenEstimator(
    string model,
    string systemPrompt,
    IReadOnlyList<AgentMessage> messages,
    IReadOnlyList<ToolDefinition> tools);

public static class ApproximateGameTokenEstimator
{
    private const long ResourceTokenEstimate = 1_200;

    public static long EstimateRequest(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model name is required.", nameof(model));
        }

        if (systemPrompt is null)
        {
            throw new ArgumentNullException(nameof(systemPrompt));
        }

        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (tools is null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        var characters = (long)systemPrompt.Length;
        foreach (var tool in tools)
        {
            if (tool is null)
            {
                throw new ArgumentException("Tool collections cannot contain null values.", nameof(tools));
            }

            characters = checked(characters
                + tool.Name.Length
                + tool.Description.Length
                + tool.InputSchemaJson.Length
                + 128);
        }

        return checked(EstimateMessages(messages) + DivideRoundUp(characters, 4));
    }

    public static long EstimateMessages(IReadOnlyList<AgentMessage> messages)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var tokens = 0L;
        foreach (var message in messages)
        {
            if (message is null)
            {
                throw new ArgumentException("Message collections cannot contain null values.", nameof(messages));
            }

            var characters = 64L;
            characters = checked(characters
                + (message.CustomRole?.Length ?? 0)
                + (message.ToolCallId?.Length ?? 0)
                + (message.ToolName?.Length ?? 0)
                + (message.DetailsJson?.Length ?? 0)
                + (message.Model?.Length ?? 0)
                + (message.ErrorMessage?.Length ?? 0));
            foreach (var pair in message.Metadata)
            {
                characters = checked(characters + pair.Key.Length + pair.Value.Length);
            }

            foreach (var content in message.Content)
            {
                switch (content)
                {
                    case TextContent text:
                        characters = checked(characters + text.Text.Length);
                        break;
                    case JsonContent json:
                        characters = checked(characters + json.Json.Length);
                        break;
                    case ReasoningContent reasoning:
                        characters = checked(characters + reasoning.Text.Length + (reasoning.Signature?.Length ?? 0));
                        break;
                    case ToolCallContent call:
                        characters = checked(characters + call.Id.Length + call.Name.Length + call.ArgumentsJson.Length);
                        break;
                    case ResourceContent resource:
                        characters = checked(characters
                            + resource.Uri.Length
                            + resource.MediaType.Length
                            + (resource.Name?.Length ?? 0));
                        tokens = checked(tokens + ResourceTokenEstimate);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported agent content type '{content.GetType().FullName}'.");
                }
            }

            tokens = checked(tokens + DivideRoundUp(characters, 4));
        }

        return tokens;
    }

    private static long DivideRoundUp(long value, long divisor) =>
        checked((value + divisor - 1) / divisor);
}

internal sealed class GameModelRecoverySafety
{
    private int _toolSideEffectObserved;
    private int _recoveryStarted;

    public GameModelRecoverySafety(bool toolSideEffectObserved)
    {
        _toolSideEffectObserved = toolSideEffectObserved ? 1 : 0;
    }

    public bool CanReplay => Volatile.Read(ref _toolSideEffectObserved) == 0;

    public bool TryBeginRecovery() =>
        CanReplay
        && Volatile.Read(ref _recoveryStarted) == 0
        && Interlocked.CompareExchange(ref _recoveryStarted, 1, 0) == 0;

    public void Record(AgentEvent agentEvent)
    {
        if (agentEvent is null)
        {
            throw new ArgumentNullException(nameof(agentEvent));
        }

        if (agentEvent.Kind is AgentEventKind.ToolStarted
            or AgentEventKind.ToolProgressed
            or AgentEventKind.ToolEnded)
        {
            Interlocked.Exchange(ref _toolSideEffectObserved, 1);
        }
    }
}

internal sealed class GameModelRecoveryCompaction
{
    public GameModelRecoveryCompaction(
        ModelRequest request,
        GameTranscriptCompactionResult compaction)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Compaction = compaction ?? throw new ArgumentNullException(nameof(compaction));
    }

    public ModelRequest Request { get; }

    public GameTranscriptCompactionResult Compaction { get; }
}

internal sealed class ContextOverflowRecoveryModelProvider : IModelProvider
{
    private readonly IModelProvider _inner;
    private readonly GameModelRecoverySafety _safety;
    private readonly int _contextWindowTokens;
    private readonly Func<ModelRequest, CancellationToken, ValueTask<GameModelRecoveryCompaction?>> _compact;
    private readonly Action<ModelUsage, string, string> _recordFailedAttemptAndSuppress;
    private readonly Action<GameTranscriptCompactionResult> _recordCompaction;
    private readonly Action<GameTranscriptCompactionException> _recordCompactionFailure;
    private readonly Action<string> _clearAssistantSuppression;

    public ContextOverflowRecoveryModelProvider(
        IModelProvider inner,
        GameModelRecoverySafety safety,
        int contextWindowTokens,
        Func<ModelRequest, CancellationToken, ValueTask<GameModelRecoveryCompaction?>> compact,
        Action<ModelUsage, string, string> recordFailedAttemptAndSuppress,
        Action<GameTranscriptCompactionResult> recordCompaction,
        Action<GameTranscriptCompactionException> recordCompactionFailure,
        Action<string> clearAssistantSuppression)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _contextWindowTokens = contextWindowTokens > 0
            ? contextWindowTokens
            : throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
        _compact = compact ?? throw new ArgumentNullException(nameof(compact));
        _recordFailedAttemptAndSuppress = recordFailedAttemptAndSuppress
            ?? throw new ArgumentNullException(nameof(recordFailedAttemptAndSuppress));
        _recordCompaction = recordCompaction ?? throw new ArgumentNullException(nameof(recordCompaction));
        _recordCompactionFailure = recordCompactionFailure ?? throw new ArgumentNullException(nameof(recordCompactionFailure));
        _clearAssistantSuppression = clearAssistantSuppression
            ?? throw new ArgumentNullException(nameof(clearAssistantSuppression));
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var activeRequest = request;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var enumerator = _inner.StreamAsync(activeRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);
            ModelStreamEvent? pendingStart = null;
            ModelResponse? recoveryResponse = null;
            Exception? recoveryException = null;
            Exception? primaryFailure = null;
            var meaningfulEventExposed = false;
            var terminalSeen = false;
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        primaryFailure = exception;
                        if (GameModelContextOverflowClassifier.IsContextOverflow(exception)
                            && TryBeginRecovery(attempt, meaningfulEventExposed))
                        {
                            recoveryException = exception;
                            break;
                        }

                        throw;
                    }

                    if (!hasNext)
                    {
                        if (pendingStart is not null)
                        {
                            yield return pendingStart;
                        }

                        yield break;
                    }

                    var current = enumerator.Current
                        ?? throw new InvalidOperationException("The model provider emitted a null stream event.");
                    if (current.Kind == ModelStreamEventKind.Started)
                    {
                        if (pendingStart is not null)
                        {
                            throw new InvalidOperationException("The model provider emitted more than one stream start event.");
                        }

                        pendingStart = current;
                        if (HasMeaningfulStartedContent(current))
                        {
                            meaningfulEventExposed = true;
                            yield return pendingStart;
                            pendingStart = null;
                        }

                        continue;
                    }

                    if (current.IsTerminal)
                    {
                        terminalSeen = true;
                        var response = current.Response
                            ?? throw new InvalidOperationException("A terminal model event did not contain a response.");
                        if (GameModelContextOverflowClassifier.IsContextOverflow(response, _contextWindowTokens)
                            && TryBeginRecovery(attempt, meaningfulEventExposed))
                        {
                            recoveryResponse = response;
                            break;
                        }

                        if (pendingStart is not null)
                        {
                            yield return pendingStart;
                        }

                        yield return current;
                        yield break;
                    }

                    meaningfulEventExposed = true;
                    if (pendingStart is not null)
                    {
                        yield return pendingStart;
                        pendingStart = null;
                    }

                    yield return current;
                }
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch when (primaryFailure is not null
                    || recoveryException is not null
                    || recoveryResponse is not null
                    || terminalSeen
                    || cancellationToken.IsCancellationRequested)
                {
                    // Stream cleanup cannot replace the primary provider outcome.
                }
            }

            var failedUsage = recoveryResponse?.Usage ?? new ModelUsage();
            if (recoveryResponse is not null)
            {
                _recordFailedAttemptAndSuppress(failedUsage, activeRequest.RunId, "provider_response");
            }

            GameModelRecoveryCompaction? compacted = null;
            GameTranscriptCompactionException? compactionFailure = null;
            Exception? unexpectedCompactionFailure = null;
            try
            {
                compacted = await _compact(activeRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GameTranscriptCompactionException exception)
            {
                compactionFailure = exception;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                unexpectedCompactionFailure = exception;
            }

            if (compactionFailure is not null)
            {
                _recordCompactionFailure(compactionFailure);
            }

            if (compacted is null || unexpectedCompactionFailure is not null)
            {
                if (recoveryResponse is not null)
                {
                    if (pendingStart is not null)
                    {
                        yield return pendingStart;
                    }

                    yield return ModelStreamEvent.Terminal(recoveryResponse);
                    yield break;
                }

                ExceptionDispatchInfo.Capture(recoveryException!).Throw();
                throw new InvalidOperationException("The provider failure could not be rethrown.");
            }

            _recordCompaction(compacted.Compaction);
            if (recoveryResponse is not null)
            {
                _clearAssistantSuppression(activeRequest.RunId);
            }

            activeRequest = compacted.Request;
        }
    }

    private bool TryBeginRecovery(int attempt, bool meaningfulEventExposed) =>
        attempt == 0
        && !meaningfulEventExposed
        && _safety.TryBeginRecovery();

    private static bool HasMeaningfulStartedContent(ModelStreamEvent streamEvent) =>
        streamEvent.Partial is { Content.Count: > 0 };
}

internal static class GameModelContextOverflowClassifier
{
    private static readonly string[] ExcludedFragments =
    {
        "rate limit",
        "rate_limit",
        "too many requests",
        "quota",
        "billing",
        "insufficient credit",
        "insufficient_credit",
        "service unavailable",
        "overloaded",
    };

    private static readonly string[] OverflowFragments =
    {
        "maximum context length",
        "context length exceeded",
        "context_length_exceeded",
        "context window exceeded",
        "context_window_exceeded",
        "model context window exceeded",
        "model_context_window_exceeded",
        "exceeds the context window",
        "exceeded the context window",
        "input is too long",
        "input_too_long",
        "prompt is too long",
        "prompt_too_long",
        "too many tokens",
        "token limit exceeded",
        "request too large for model",
        "reduce the length of the messages",
        "exceeds the maximum number of tokens allowed",
        "maximum prompt length",
        "maximum allowed input length",
        "longer than the model's context length",
        "longer than the model context length",
        "exceeds the available context size",
        "greater than the context length",
        "context window exceeds limit",
        "exceeded model token limit",
        "configured context size",
        "range of input length should be",
        "400 status code (no body)",
        "413 status code (no body)",
    };

    private static readonly HashSet<string> StructuredOverflowCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "context_length_exceeded",
        "context_window_exceeded",
        "model_context_window_exceeded",
        "input_too_long",
        "prompt_too_long",
        "request_too_large",
    };

    public static bool IsContextOverflow(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        if (exception is OperationCanceledException)
        {
            return false;
        }

        if (exception is ModelProviderException providerFailure)
        {
            if (providerFailure.StatusCode == 429 || ContainsExcluded(providerFailure.Message))
            {
                return false;
            }

            if (HasStructuredOverflow(providerFailure.Diagnostics))
            {
                return true;
            }

            if (providerFailure.StatusCode == 413)
            {
                return true;
            }

            if (providerFailure.StatusCode is not null
                && providerFailure.StatusCode is not 400 and not 422)
            {
                return false;
            }
        }
        else if (ContainsExcluded(exception.Message))
        {
            return false;
        }

        return ContainsOverflow(exception.Message);
    }

    public static bool IsContextOverflow(ModelResponse response, int contextWindowTokens)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        if (contextWindowTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
        }

        var diagnosticText = string.Join(" ", response.Diagnostics.Select(diagnostic => diagnostic.Message));
        if (ContainsExcluded(response.ErrorMessage)
            || ContainsExcluded(response.RawStopReason)
            || ContainsExcluded(diagnosticText))
        {
            return false;
        }

        if (HasStructuredOverflow(response.Diagnostics)
            || IsStructuredOverflowCode(response.RawStopReason))
        {
            return response.Content.Count == 0;
        }

        if (response.StopReason == ModelStopReason.Error)
        {
            return response.Content.Count == 0
                && (ContainsOverflow(response.ErrorMessage) || ContainsOverflow(diagnosticText));
        }

        if (response.Content.Count != 0 || response.Usage.OutputTokens != 0)
        {
            return false;
        }

        var inputTokens = checked(response.Usage.InputTokens + response.Usage.CacheReadTokens);
        if (response.StopReason == ModelStopReason.Length)
        {
            var threshold = checked((contextWindowTokens * 99L + 99L) / 100L);
            return inputTokens >= threshold;
        }

        return response.StopReason == ModelStopReason.Stop && inputTokens > contextWindowTokens;
    }

    private static bool HasStructuredOverflow(IReadOnlyList<ModelDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (IsStructuredOverflowCode(diagnostic.Code))
            {
                return true;
            }

            if (diagnostic.DataJson is { } data && HasStructuredOverflowJson(data))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasStructuredOverflowJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return HasStructuredOverflowJson(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasStructuredOverflowJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && IsDiagnosticCodeProperty(property.Name)
                    && IsStructuredOverflowCode(property.Value.GetString()))
                {
                    return true;
                }

                if (HasStructuredOverflowJson(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasStructuredOverflowJson(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDiagnosticCodeProperty(string value) =>
        string.Equals(value, "code", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "errorCode", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "type", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "reason", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "stop_reason", StringComparison.OrdinalIgnoreCase);

    private static bool IsStructuredOverflowCode(string? value) =>
        value is not null && StructuredOverflowCodes.Contains(value.Trim());

    private static bool ContainsExcluded(string? value) => ContainsAny(value, ExcludedFragments);

    private static bool ContainsOverflow(string? value) => ContainsAny(value, OverflowFragments);

    private static bool ContainsAny(string? value, IReadOnlyList<string> fragments)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var fragment in fragments)
        {
            if (value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}

public interface IGameTranscriptCompactor
{
    ValueTask<GameTranscriptCompactionResult> CompactAsync(
        GameTranscriptCompactionContext context,
        CancellationToken cancellationToken);
}

public sealed class GameTranscriptSummaryResult
{
    public GameTranscriptSummaryResult(
        string summary,
        ModelUsage? usage = null,
        string? detailsJson = null)
    {
        Summary = string.IsNullOrWhiteSpace(summary)
            ? throw new ArgumentException("A transcript summary is required.", nameof(summary))
            : summary;
        Usage = usage ?? new ModelUsage();
        DetailsJson = detailsJson is null ? null : GameJson.RequireValid(detailsJson, nameof(detailsJson));
    }

    public string Summary { get; }

    public ModelUsage Usage { get; }

    public string? DetailsJson { get; }
}

public enum GameTranscriptSummaryPurpose
{
    Compaction,
    Branch,
}

public sealed class GameTranscriptSummaryContext
{
    internal GameTranscriptSummaryContext(
        GameSessionKey session,
        IReadOnlyList<AgentMessage> sourceMessages,
        IReadOnlyList<AgentMessage> messages,
        string? previousSummary,
        GameTranscriptSummaryPurpose purpose,
        int attempt,
        string? previousError,
        long? targetEstimatedTokens)
    {
        Session = session.EnsureValid(nameof(session));
        SourceMessages = CopyMessages(sourceMessages, nameof(sourceMessages));
        Messages = CopyMessages(messages, nameof(messages));
        PreviousSummary = string.IsNullOrWhiteSpace(previousSummary) ? null : previousSummary;
        if (!Enum.IsDefined(typeof(GameTranscriptSummaryPurpose), purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        Purpose = purpose;
        Attempt = attempt > 0 ? attempt : throw new ArgumentOutOfRangeException(nameof(attempt));
        PreviousError = string.IsNullOrWhiteSpace(previousError) ? null : previousError;
        TargetEstimatedTokens = targetEstimatedTokens is > 0
            ? targetEstimatedTokens
            : targetEstimatedTokens is null
                ? null
                : throw new ArgumentOutOfRangeException(nameof(targetEstimatedTokens));
    }

    public GameSessionKey Session { get; }

    /// <summary>
    /// The complete source range being replaced. This includes the prior summary message, when present.
    /// </summary>
    public IReadOnlyList<AgentMessage> SourceMessages { get; }

    /// <summary>
    /// New history to merge into <see cref="PreviousSummary"/>. It never contains the prior summary message itself.
    /// </summary>
    public IReadOnlyList<AgentMessage> Messages { get; }

    public string? PreviousSummary { get; }

    public GameTranscriptSummaryPurpose Purpose { get; }

    public int Attempt { get; }

    public string? PreviousError { get; }

    public long? TargetEstimatedTokens { get; }

    private static IReadOnlyList<AgentMessage> CopyMessages(
        IReadOnlyList<AgentMessage> messages,
        string parameterName)
    {
        var copied = (messages ?? throw new ArgumentNullException(parameterName)).ToArray();
        if (copied.Any(message => message is null))
        {
            throw new ArgumentException("Summary message collections cannot contain null values.", parameterName);
        }

        return Array.AsReadOnly(copied);
    }
}

public sealed class GameTranscriptSummaryAttemptResult
{
    private GameTranscriptSummaryAttemptResult(
        bool succeeded,
        string? summary,
        ModelUsage? usage,
        string? error,
        bool retryable,
        string? detailsJson)
    {
        if (succeeded == string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("A successful summary attempt requires summary text, and a failed attempt cannot include it.");
        }

        if (!succeeded && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A failed summary attempt requires an error message.", nameof(error));
        }

        Succeeded = succeeded;
        Summary = succeeded ? summary : null;
        Usage = usage ?? new ModelUsage();
        Error = succeeded ? null : error;
        Retryable = !succeeded && retryable;
        DetailsJson = detailsJson is null ? null : GameJson.RequireValid(detailsJson, nameof(detailsJson));
    }

    public bool Succeeded { get; }

    public string? Summary { get; }

    public ModelUsage Usage { get; }

    public string? Error { get; }

    public bool Retryable { get; }

    public string? DetailsJson { get; }

    public static GameTranscriptSummaryAttemptResult Success(
        string summary,
        ModelUsage? usage = null,
        string? detailsJson = null) =>
        new(true, summary, usage, error: null, retryable: false, detailsJson);

    public static GameTranscriptSummaryAttemptResult Success(GameTranscriptSummaryResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        return Success(result.Summary, result.Usage, result.DetailsJson);
    }

    public static GameTranscriptSummaryAttemptResult Failure(
        string error,
        ModelUsage? usage = null,
        bool retryable = false,
        string? detailsJson = null) =>
        new(false, summary: null, usage, error, retryable, detailsJson);
}

public delegate ValueTask<GameTranscriptSummaryAttemptResult> GameTranscriptSummaryAttemptHandler(
    GameTranscriptSummaryContext context,
    CancellationToken cancellationToken);

public sealed class GameTranscriptSummaryAttemptDetails
{
    public GameTranscriptSummaryAttemptDetails(
        int attempt,
        bool succeeded,
        bool retryable,
        ModelUsage? usage = null,
        string? error = null,
        string? detailsJson = null)
    {
        Attempt = attempt > 0 ? attempt : throw new ArgumentOutOfRangeException(nameof(attempt));
        if (succeeded && !string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A successful summary attempt cannot include an error.", nameof(error));
        }

        if (!succeeded && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A failed summary attempt requires an error.", nameof(error));
        }

        Succeeded = succeeded;
        Retryable = !succeeded && retryable;
        Usage = usage ?? new ModelUsage();
        Error = succeeded ? null : error;
        DetailsJson = detailsJson is null ? null : GameJson.RequireValid(detailsJson, nameof(detailsJson));
    }

    public int Attempt { get; }

    public bool Succeeded { get; }

    public bool Retryable { get; }

    public ModelUsage Usage { get; }

    public string? Error { get; }

    public string? DetailsJson { get; }
}

public enum GameTranscriptCompactionTrigger
{
    None,
    MessageLimit,
    TokenLimit,
    MessageAndTokenLimit,
}

public sealed class GameTranscriptCompactionDetails
{
    public GameTranscriptCompactionDetails(
        int originalMessageCount,
        int compactedMessageCount,
        int retainedMessageCount,
        long? estimatedTokensBefore = null,
        string? summaryDetailsJson = null,
        GameTranscriptCompactionTrigger trigger = GameTranscriptCompactionTrigger.None,
        int? cutMessageIndex = null,
        int incrementalMessageCount = 0,
        int retainedTurnCount = 0,
        bool previousSummaryUsed = false,
        IReadOnlyList<GameTranscriptSummaryAttemptDetails>? summaryAttempts = null,
        bool applied = true,
        string? failureCode = null)
    {
        if (originalMessageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalMessageCount));
        }

        if (compactedMessageCount < 0 || compactedMessageCount > originalMessageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(compactedMessageCount));
        }

        if (retainedMessageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedMessageCount));
        }

        if (checked(compactedMessageCount + retainedMessageCount) != originalMessageCount)
        {
            throw new ArgumentException("Compacted and retained message counts must partition the original transcript.");
        }

        if (estimatedTokensBefore is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedTokensBefore));
        }

        if (!Enum.IsDefined(typeof(GameTranscriptCompactionTrigger), trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(trigger));
        }

        if (cutMessageIndex is < 0 || cutMessageIndex > originalMessageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(cutMessageIndex));
        }

        if (incrementalMessageCount < 0 || incrementalMessageCount > compactedMessageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(incrementalMessageCount));
        }

        if (retainedTurnCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedTurnCount));
        }

        var copiedAttempts = (summaryAttempts ?? Array.Empty<GameTranscriptSummaryAttemptDetails>()).ToArray();
        if (copiedAttempts.Any(attempt => attempt is null))
        {
            throw new ArgumentException("Summary attempt collections cannot contain null values.", nameof(summaryAttempts));
        }

        if (copiedAttempts.Select(attempt => attempt.Attempt).Distinct().Count() != copiedAttempts.Length
            || copiedAttempts.Where((attempt, index) => attempt.Attempt != index + 1).Any())
        {
            throw new ArgumentException("Summary attempts must be ordered and consecutively numbered.", nameof(summaryAttempts));
        }

        if (applied && failureCode is not null)
        {
            throw new ArgumentException("An applied compaction cannot include a failure code.", nameof(failureCode));
        }

        OriginalMessageCount = originalMessageCount;
        CompactedMessageCount = compactedMessageCount;
        RetainedMessageCount = retainedMessageCount;
        EstimatedTokensBefore = estimatedTokensBefore;
        SummaryDetailsJson = summaryDetailsJson is null
            ? null
            : GameJson.RequireValid(summaryDetailsJson, nameof(summaryDetailsJson));
        Trigger = trigger;
        CutMessageIndex = cutMessageIndex;
        IncrementalMessageCount = incrementalMessageCount;
        RetainedTurnCount = retainedTurnCount;
        PreviousSummaryUsed = previousSummaryUsed;
        SummaryAttempts = Array.AsReadOnly(copiedAttempts);
        Applied = applied;
        FailureCode = failureCode is null ? null : GameJson.RequireId(failureCode, nameof(failureCode));
    }

    public int OriginalMessageCount { get; }

    public int CompactedMessageCount { get; }

    public int RetainedMessageCount { get; }

    public long? EstimatedTokensBefore { get; }

    public string? SummaryDetailsJson { get; }

    public GameTranscriptCompactionTrigger Trigger { get; }

    public int? CutMessageIndex { get; }

    public int IncrementalMessageCount { get; }

    public int RetainedTurnCount { get; }

    public bool PreviousSummaryUsed { get; }

    public IReadOnlyList<GameTranscriptSummaryAttemptDetails> SummaryAttempts { get; }

    public int SummaryAttemptCount => SummaryAttempts.Count;

    public int FailedSummaryAttemptCount => SummaryAttempts.Count(attempt => !attempt.Succeeded);

    public bool Applied { get; }

    public string? FailureCode { get; }
}

public sealed class GameTranscriptCompactionException : InvalidOperationException
{
    public GameTranscriptCompactionException(
        string errorCode,
        string message,
        ModelUsage? usage,
        GameTranscriptCompactionDetails details,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = GameJson.RequireId(errorCode, nameof(errorCode));
        Usage = usage ?? new ModelUsage();
        Details = details ?? throw new ArgumentNullException(nameof(details));
    }

    public string ErrorCode { get; }

    public ModelUsage Usage { get; }

    public GameTranscriptCompactionDetails Details { get; }
}

public sealed class GameTranscriptCompactionResult
{
    public GameTranscriptCompactionResult(
        IReadOnlyList<AgentMessage> messages,
        ModelUsage? usage,
        GameTranscriptCompactionDetails details)
    {
        var copiedMessages = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
        if (copiedMessages.Any(message => message is null))
        {
            throw new ArgumentException("A compacted transcript cannot contain null messages.", nameof(messages));
        }

        Messages = Array.AsReadOnly(copiedMessages);
        Usage = usage ?? new ModelUsage();
        Details = details ?? throw new ArgumentNullException(nameof(details));
    }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public ModelUsage Usage { get; }

    public GameTranscriptCompactionDetails Details { get; }
}

public delegate ValueTask<GameTranscriptSummaryResult> GameTranscriptSummarizer(
    GameSessionKey session,
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);

public sealed class SummarizingGameTranscriptCompactor : IGameTranscriptCompactor
{
    private const string SummaryRole = "transcript_summary";
    private readonly GameTranscriptSummaryAttemptHandler _summarizer;
    private readonly int _maxSummaryAttempts;

    public SummarizingGameTranscriptCompactor(GameTranscriptSummarizer summarizer)
    {
        if (summarizer is null)
        {
            throw new ArgumentNullException(nameof(summarizer));
        }

        _maxSummaryAttempts = 1;
        _summarizer = async (request, cancellationToken) =>
        {
            var result = await summarizer(
                request.Session,
                request.SourceMessages,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The transcript summarizer returned null.");
            return GameTranscriptSummaryAttemptResult.Success(result);
        };
    }

    public SummarizingGameTranscriptCompactor(
        GameTranscriptSummaryAttemptHandler summarizer,
        int maxSummaryAttempts = 3)
    {
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
        _maxSummaryAttempts = maxSummaryAttempts > 0
            ? maxSummaryAttempts
            : throw new ArgumentOutOfRangeException(nameof(maxSummaryAttempts));
    }

    public async ValueTask<GameTranscriptCompactionResult> CompactAsync(
        GameTranscriptCompactionContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var estimatedTokensBefore = EstimateBefore(context);
        if (Fits(context, context.Messages))
        {
            return new GameTranscriptCompactionResult(
                context.Messages,
                new ModelUsage(),
                new GameTranscriptCompactionDetails(
                    context.Messages.Count,
                    compactedMessageCount: 0,
                    retainedMessageCount: context.Messages.Count,
                    estimatedTokensBefore));
        }

        GameTranscriptStructure.ValidateToolExchanges(context.Messages);
        var trigger = GetTrigger(context, estimatedTokensBefore);
        var keepCount = Math.Max(1, context.TargetMessageCount - 1);
        var start = FindSafeSuffixStart(context, keepCount);
        var removed = context.Messages.Take(start).ToArray();
        var previousSummaryIndex = FindPreviousSummaryIndex(removed);
        var previousSummary = previousSummaryIndex < 0
            ? null
            : ReadSummary(removed[previousSummaryIndex]);
        var incrementalMessages = removed
            .Where((_, index) => index != previousSummaryIndex)
            .ToArray();
        var retained = context.Messages.Skip(start).ToArray();
        var retainedTurnCount = GameTranscriptStructure.CountTurns(retained);
        var attempts = new List<GameTranscriptSummaryAttemptDetails>();
        var usages = new List<ModelUsage>();
        string? previousError = null;

        for (var attempt = 1; attempt <= _maxSummaryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptResult = await _summarizer(
                new GameTranscriptSummaryContext(
                    context.Session,
                    removed,
                    incrementalMessages,
                    previousSummary,
                    GameTranscriptSummaryPurpose.Compaction,
                    attempt,
                    previousError,
                    context.TargetEstimatedTokens),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The transcript summarizer returned null.");
            usages.Add(attemptResult.Usage);

            if (!attemptResult.Succeeded)
            {
                previousError = attemptResult.Error!;
                attempts.Add(new GameTranscriptSummaryAttemptDetails(
                    attempt,
                    succeeded: false,
                    attemptResult.Retryable,
                    attemptResult.Usage,
                    previousError,
                    attemptResult.DetailsJson));
                var usageLimitReached = IsSummaryUsageLimitReached(context, usages);
                if (attemptResult.Retryable && attempt < _maxSummaryAttempts && !usageLimitReached)
                {
                    continue;
                }

                throw CreateFailure(
                    usageLimitReached && attemptResult.Retryable
                        ? "summary_usage_limit_exceeded"
                        : "summary_failed",
                    usageLimitReached && attemptResult.Retryable
                        ? previousError + " No retry was started because the summary usage budget was exhausted."
                        : previousError,
                    context,
                    removed.Length,
                    start,
                    incrementalMessages.Length,
                    retainedTurnCount,
                    previousSummary is not null,
                    trigger,
                    estimatedTokensBefore,
                    usages,
                    attempts,
                    attemptResult.DetailsJson);
            }

            var summaryMessage = CreateSummaryMessage(attemptResult.Summary!, removed.Length);
            var result = new[] { summaryMessage }.Concat(retained).ToArray();
            GameTranscriptStructure.ValidateToolExchanges(result);
            var targetError = result.Length > context.TargetMessageCount
                ? "The generated summary exceeded the requested message target."
                : context.TargetEstimatedTokens is { } tokenTarget
                    && Estimate(context, result) > tokenTarget
                        ? "The generated summary exceeded the requested token target."
                        : null;
            if (targetError is not null)
            {
                previousError = targetError;
                attempts.Add(new GameTranscriptSummaryAttemptDetails(
                    attempt,
                    succeeded: false,
                    retryable: true,
                    attemptResult.Usage,
                    targetError,
                    attemptResult.DetailsJson));
                var usageLimitReached = IsSummaryUsageLimitReached(context, usages);
                if (attempt < _maxSummaryAttempts && !usageLimitReached)
                {
                    continue;
                }

                throw CreateFailure(
                    usageLimitReached
                        ? "summary_usage_limit_exceeded"
                        : "summary_target_exceeded",
                    usageLimitReached
                        ? targetError + " No retry was started because the summary usage budget was exhausted."
                        : targetError,
                    context,
                    removed.Length,
                    start,
                    incrementalMessages.Length,
                    retainedTurnCount,
                    previousSummary is not null,
                    trigger,
                    estimatedTokensBefore,
                    usages,
                    attempts,
                    attemptResult.DetailsJson);
            }

            attempts.Add(new GameTranscriptSummaryAttemptDetails(
                attempt,
                succeeded: true,
                retryable: false,
                attemptResult.Usage,
                detailsJson: attemptResult.DetailsJson));
            var details = CreateDetails(
                context,
                removed.Length,
                start,
                incrementalMessages.Length,
                retainedTurnCount,
                previousSummary is not null,
                trigger,
                estimatedTokensBefore,
                attempts,
                attemptResult.DetailsJson);
            return new GameTranscriptCompactionResult(
                result,
                GameTranscriptSummaryUtilities.AggregateUsage(usages),
                details);
        }

        throw new InvalidOperationException("The transcript summary attempt loop ended unexpectedly.");
    }

    private static long? EstimateBefore(GameTranscriptCompactionContext context) =>
        context.TokenEstimator is null ? null : Estimate(context, context.Messages);

    private static int FindSafeSuffixStart(GameTranscriptCompactionContext context, int keepCount)
    {
        var messages = context.Messages;
        var desired = Math.Max(1, messages.Count - keepCount);
        for (var index = desired; index < messages.Count; index++)
        {
            if (GameTranscriptStructure.IsCompleteTurnBoundary(messages, index))
            {
                var projectedCount = checked(messages.Count - index + 1);
                if (projectedCount > context.TargetMessageCount)
                {
                    continue;
                }

                if (context.TargetEstimatedTokens is { } tokenTarget)
                {
                    // Leave half of the transcript budget available to the summary. This is
                    // conservative and the completed summary is checked again below.
                    var suffixTarget = Math.Max(1, tokenTarget / 2);
                    if (Estimate(context, messages.Skip(index).ToArray()) > suffixTarget)
                    {
                        continue;
                    }
                }

                return index;
            }
        }

        // If no complete turn fits, replace the entire transcript with one summary.
        return messages.Count;
    }

    private static bool Fits(GameTranscriptCompactionContext context, IReadOnlyList<AgentMessage> messages) =>
        messages.Count <= context.TargetMessageCount
        && (context.TargetEstimatedTokens is not { } target || Estimate(context, messages) <= target);

    private static long Estimate(GameTranscriptCompactionContext context, IReadOnlyList<AgentMessage> messages)
    {
        var estimate = context.TokenEstimator!(messages);
        return estimate >= 0
            ? estimate
            : throw new InvalidOperationException("The transcript token estimator returned a negative value.");
    }

    private static GameTranscriptCompactionTrigger GetTrigger(
        GameTranscriptCompactionContext context,
        long? estimatedTokensBefore)
    {
        var messageLimit = context.Messages.Count > context.TargetMessageCount;
        var tokenLimit = context.TargetEstimatedTokens is { } tokenTarget
            && estimatedTokensBefore > tokenTarget;
        return (messageLimit, tokenLimit) switch
        {
            (true, true) => GameTranscriptCompactionTrigger.MessageAndTokenLimit,
            (true, false) => GameTranscriptCompactionTrigger.MessageLimit,
            (false, true) => GameTranscriptCompactionTrigger.TokenLimit,
            _ => GameTranscriptCompactionTrigger.None,
        };
    }

    private static int FindPreviousSummaryIndex(IReadOnlyList<AgentMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == AgentRole.Custom
                && string.Equals(messages[index].CustomRole, SummaryRole, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string ReadSummary(AgentMessage message)
    {
        var summary = string.Join("\n", message.Content.OfType<TextContent>().Select(content => content.Text));
        return string.IsNullOrWhiteSpace(summary)
            ? throw new InvalidOperationException("A prior transcript summary did not contain summary text.")
            : summary;
    }

    private static AgentMessage CreateSummaryMessage(string summary, int compactedMessageCount) => new(
        AgentRole.Custom,
        new AgentContent[] { new TextContent(summary) },
        DateTimeOffset.UtcNow,
        customRole: SummaryRole,
        metadata: new Dictionary<string, string>
        {
            ["game.compacted_message_count"] = compactedMessageCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        });

    private static GameTranscriptCompactionDetails CreateDetails(
        GameTranscriptCompactionContext context,
        int compactedMessageCount,
        int cutMessageIndex,
        int incrementalMessageCount,
        int retainedTurnCount,
        bool previousSummaryUsed,
        GameTranscriptCompactionTrigger trigger,
        long? estimatedTokensBefore,
        IReadOnlyList<GameTranscriptSummaryAttemptDetails> attempts,
        string? summaryDetailsJson,
        bool applied = true,
        string? failureCode = null) => new(
        context.Messages.Count,
        compactedMessageCount,
        context.Messages.Count - compactedMessageCount,
        estimatedTokensBefore,
        summaryDetailsJson,
        trigger,
        cutMessageIndex,
        incrementalMessageCount,
        retainedTurnCount,
        previousSummaryUsed,
        attempts,
        applied,
        failureCode);

    private static GameTranscriptCompactionException CreateFailure(
        string errorCode,
        string error,
        GameTranscriptCompactionContext context,
        int compactedMessageCount,
        int cutMessageIndex,
        int incrementalMessageCount,
        int retainedTurnCount,
        bool previousSummaryUsed,
        GameTranscriptCompactionTrigger trigger,
        long? estimatedTokensBefore,
        IReadOnlyList<ModelUsage> usages,
        IReadOnlyList<GameTranscriptSummaryAttemptDetails> attempts,
        string? summaryDetailsJson) => new(
        errorCode,
        error,
        GameTranscriptSummaryUtilities.AggregateUsage(usages),
        CreateDetails(
            context,
            compactedMessageCount,
            cutMessageIndex,
            incrementalMessageCount,
            retainedTurnCount,
            previousSummaryUsed,
            trigger,
            estimatedTokensBefore,
            attempts,
            summaryDetailsJson,
            applied: false,
            failureCode: errorCode));

    private static bool IsSummaryUsageLimitReached(
        GameTranscriptCompactionContext context,
        IReadOnlyList<ModelUsage> usages) =>
        context.MaximumSummaryUsageTokens is { } maximum
        && GameTranscriptSummaryUtilities.AggregateUsage(usages).TotalTokens >= maximum;
}

public sealed class GameBranchSummaryDetails
{
    public GameBranchSummaryDetails(
        int sourceMessageCount,
        int summarizedMessageCount,
        long? estimatedSourceTokens,
        IReadOnlyList<GameTranscriptSummaryAttemptDetails>? summaryAttempts = null)
    {
        if (sourceMessageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceMessageCount));
        }

        if (summarizedMessageCount < 0 || summarizedMessageCount > sourceMessageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(summarizedMessageCount));
        }

        if (estimatedSourceTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedSourceTokens));
        }

        var copiedAttempts = (summaryAttempts ?? Array.Empty<GameTranscriptSummaryAttemptDetails>()).ToArray();
        if (copiedAttempts.Any(attempt => attempt is null)
            || copiedAttempts.Where((attempt, index) => attempt.Attempt != index + 1).Any())
        {
            throw new ArgumentException(
                "Summary attempts must be non-null, ordered, and consecutively numbered.",
                nameof(summaryAttempts));
        }

        SourceMessageCount = sourceMessageCount;
        SummarizedMessageCount = summarizedMessageCount;
        EstimatedSourceTokens = estimatedSourceTokens;
        SummaryAttempts = Array.AsReadOnly(copiedAttempts);
    }

    public int SourceMessageCount { get; }

    public int SummarizedMessageCount { get; }

    public int OmittedMessageCount => SourceMessageCount - SummarizedMessageCount;

    public long? EstimatedSourceTokens { get; }

    public IReadOnlyList<GameTranscriptSummaryAttemptDetails> SummaryAttempts { get; }

    public int SummaryAttemptCount => SummaryAttempts.Count;
}

public sealed class GameBranchSummaryResult
{
    public GameBranchSummaryResult(
        string summary,
        IReadOnlyList<AgentMessage> summarizedMessages,
        ModelUsage? usage,
        GameBranchSummaryDetails details,
        string? summaryDetailsJson = null)
    {
        Summary = string.IsNullOrWhiteSpace(summary)
            ? throw new ArgumentException("A branch summary is required.", nameof(summary))
            : summary;
        var copiedMessages = (summarizedMessages ?? throw new ArgumentNullException(nameof(summarizedMessages))).ToArray();
        if (copiedMessages.Any(message => message is null))
        {
            throw new ArgumentException("Branch summary messages cannot contain null values.", nameof(summarizedMessages));
        }

        SummarizedMessages = Array.AsReadOnly(copiedMessages);
        Usage = usage ?? new ModelUsage();
        Details = details ?? throw new ArgumentNullException(nameof(details));
        SummaryDetailsJson = summaryDetailsJson is null
            ? null
            : GameJson.RequireValid(summaryDetailsJson, nameof(summaryDetailsJson));
    }

    public string Summary { get; }

    public IReadOnlyList<AgentMessage> SummarizedMessages { get; }

    public ModelUsage Usage { get; }

    public GameBranchSummaryDetails Details { get; }

    public string? SummaryDetailsJson { get; }
}

public sealed class GameBranchSummaryException : InvalidOperationException
{
    public GameBranchSummaryException(
        string message,
        ModelUsage? usage,
        GameBranchSummaryDetails details)
        : base(message)
    {
        Usage = usage ?? new ModelUsage();
        Details = details ?? throw new ArgumentNullException(nameof(details));
    }

    public ModelUsage Usage { get; }

    public GameBranchSummaryDetails Details { get; }
}

/// <summary>
/// Summarizes an abandoned linear branch supplied by the host. It has no dependency on a session-tree implementation.
/// </summary>
public sealed class GameBranchSummarizer
{
    private readonly GameTranscriptSummaryAttemptHandler _summarizer;
    private readonly int _maxSummaryAttempts;

    public GameBranchSummarizer(GameTranscriptSummarizer summarizer)
    {
        if (summarizer is null)
        {
            throw new ArgumentNullException(nameof(summarizer));
        }

        _maxSummaryAttempts = 1;
        _summarizer = async (request, cancellationToken) =>
        {
            var result = await summarizer(
                request.Session,
                request.SourceMessages,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The branch summarizer returned null.");
            return GameTranscriptSummaryAttemptResult.Success(result);
        };
    }

    public GameBranchSummarizer(
        GameTranscriptSummaryAttemptHandler summarizer,
        int maxSummaryAttempts = 3)
    {
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
        _maxSummaryAttempts = maxSummaryAttempts > 0
            ? maxSummaryAttempts
            : throw new ArgumentOutOfRangeException(nameof(maxSummaryAttempts));
    }

    public ValueTask<GameBranchSummaryResult> SummarizeAsync(
        GameSessionKey session,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default) =>
        SummarizeCoreAsync(session, messages, targetEstimatedTokens: null, tokenEstimator: null, cancellationToken);

    public ValueTask<GameBranchSummaryResult> SummarizeAsync(
        GameSessionKey session,
        IReadOnlyList<AgentMessage> messages,
        long targetEstimatedTokens,
        GameTranscriptTokenEstimator tokenEstimator,
        CancellationToken cancellationToken = default) =>
        SummarizeCoreAsync(session, messages, targetEstimatedTokens, tokenEstimator, cancellationToken);

    private async ValueTask<GameBranchSummaryResult> SummarizeCoreAsync(
        GameSessionKey session,
        IReadOnlyList<AgentMessage> messages,
        long? targetEstimatedTokens,
        GameTranscriptTokenEstimator? tokenEstimator,
        CancellationToken cancellationToken)
    {
        session.EnsureValid(nameof(session));
        var source = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
        if (source.Length == 0 || source.Any(message => message is null))
        {
            throw new ArgumentException("A branch summary requires non-null messages.", nameof(messages));
        }

        if (targetEstimatedTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetEstimatedTokens));
        }

        if (targetEstimatedTokens is not null && tokenEstimator is null)
        {
            throw new ArgumentException(
                "A token estimator is required when a branch-summary token target is configured.",
                nameof(tokenEstimator));
        }

        GameTranscriptStructure.ValidateToolExchanges(source);
        long? estimatedSourceTokens = tokenEstimator is null
            ? null
            : GameTranscriptSummaryUtilities.ValidateEstimate(tokenEstimator(source));
        var selected = SelectBranchMessages(source, targetEstimatedTokens, tokenEstimator);
        var attempts = new List<GameTranscriptSummaryAttemptDetails>();
        var usages = new List<ModelUsage>();
        string? previousError = null;
        for (var attempt = 1; attempt <= _maxSummaryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptResult = await _summarizer(
                new GameTranscriptSummaryContext(
                    session,
                    selected,
                    selected,
                    previousSummary: null,
                    GameTranscriptSummaryPurpose.Branch,
                    attempt,
                    previousError,
                    targetEstimatedTokens),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The branch summarizer returned null.");
            usages.Add(attemptResult.Usage);
            if (attemptResult.Succeeded)
            {
                attempts.Add(new GameTranscriptSummaryAttemptDetails(
                    attempt,
                    succeeded: true,
                    retryable: false,
                    attemptResult.Usage,
                    detailsJson: attemptResult.DetailsJson));
                return new GameBranchSummaryResult(
                    attemptResult.Summary!,
                    selected,
                    GameTranscriptSummaryUtilities.AggregateUsage(usages),
                    new GameBranchSummaryDetails(source.Length, selected.Length, estimatedSourceTokens, attempts),
                    attemptResult.DetailsJson);
            }

            previousError = attemptResult.Error!;
            attempts.Add(new GameTranscriptSummaryAttemptDetails(
                attempt,
                succeeded: false,
                attemptResult.Retryable,
                attemptResult.Usage,
                previousError,
                attemptResult.DetailsJson));
            if (!attemptResult.Retryable || attempt == _maxSummaryAttempts)
            {
                throw new GameBranchSummaryException(
                    previousError,
                    GameTranscriptSummaryUtilities.AggregateUsage(usages),
                    new GameBranchSummaryDetails(source.Length, selected.Length, estimatedSourceTokens, attempts));
            }
        }

        throw new InvalidOperationException("The branch summary attempt loop ended unexpectedly.");
    }

    private static AgentMessage[] SelectBranchMessages(
        IReadOnlyList<AgentMessage> messages,
        long? targetEstimatedTokens,
        GameTranscriptTokenEstimator? tokenEstimator)
    {
        if (targetEstimatedTokens is null
            || GameTranscriptSummaryUtilities.ValidateEstimate(tokenEstimator!(messages)) <= targetEstimatedTokens)
        {
            return messages.ToArray();
        }

        for (var index = 1; index < messages.Count; index++)
        {
            if (!GameTranscriptStructure.IsCompleteTurnBoundary(messages, index))
            {
                continue;
            }

            var candidate = messages.Skip(index).ToArray();
            if (GameTranscriptSummaryUtilities.ValidateEstimate(tokenEstimator(candidate)) <= targetEstimatedTokens)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No complete branch turn fits the requested summary input budget.");
    }
}

internal static class GameTranscriptStructure
{
    public static bool IsCompleteTurnBoundary(IReadOnlyList<AgentMessage> messages, int index)
    {
        if (index <= 0 || index >= messages.Count
            || messages[index].Role is not (AgentRole.User or AgentRole.Custom))
        {
            return false;
        }

        return HasCompleteToolExchanges(messages, 0, index)
            && HasCompleteToolExchanges(messages, index, messages.Count);
    }

    public static void ValidateToolExchanges(IReadOnlyList<AgentMessage> messages)
    {
        if (!HasCompleteToolExchanges(messages, 0, messages.Count))
        {
            throw new InvalidOperationException(
                "The transcript contains an orphan, unresolved, or duplicate tool exchange.");
        }
    }

    public static int CountTurns(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count == 0)
        {
            return 0;
        }

        var starts = messages.Count(message => message.Role is AgentRole.User or AgentRole.Custom);
        return starts == 0 ? 1 : starts;
    }

    private static bool HasCompleteToolExchanges(
        IReadOnlyList<AgentMessage> messages,
        int start,
        int end)
    {
        var openCalls = new HashSet<string>(StringComparer.Ordinal);
        var seenCalls = new HashSet<string>(StringComparer.Ordinal);
        for (var index = start; index < end; index++)
        {
            var message = messages[index];
            if (message.Role == AgentRole.Assistant)
            {
                foreach (var call in message.Content.OfType<ToolCallContent>())
                {
                    if (!seenCalls.Add(call.Id) || !openCalls.Add(call.Id))
                    {
                        return false;
                    }
                }
            }
            else if (message.Role == AgentRole.Tool)
            {
                if (message.ToolCallId is not { } callId || !openCalls.Remove(callId))
                {
                    return false;
                }
            }
        }

        return openCalls.Count == 0;
    }
}

internal static class GameTranscriptSummaryUtilities
{
    public static long ValidateEstimate(long estimate) => estimate >= 0
        ? estimate
        : throw new InvalidOperationException("The transcript token estimator returned a negative value.");

    public static ModelUsage AggregateUsage(IEnumerable<ModelUsage> values)
    {
        var usages = values.ToArray();
        if (usages.Length == 0)
        {
            return new ModelUsage();
        }

        if (usages.Length == 1)
        {
            return usages[0];
        }

        var hasReasoning = usages.Any(usage => usage.ReasoningTokens is not null);
        var hasLongCacheWrite = usages.Any(usage => usage.CacheWriteOneHourTokens is not null);
        return new ModelUsage(
            usages.Aggregate(0L, (total, usage) => checked(total + usage.InputTokens)),
            usages.Aggregate(0L, (total, usage) => checked(total + usage.OutputTokens)),
            usages.Aggregate(0L, (total, usage) => checked(total + usage.CacheReadTokens)),
            usages.Aggregate(0L, (total, usage) => checked(total + usage.CacheWriteTokens)),
            hasReasoning
                ? usages.Aggregate(0L, (total, usage) => checked(total + (usage.ReasoningTokens ?? 0)))
                : null,
            hasLongCacheWrite
                ? usages.Aggregate(0L, (total, usage) => checked(total + (usage.CacheWriteOneHourTokens ?? 0)))
                : null,
            new ModelCost(
                usages.Sum(usage => usage.Cost.Input),
                usages.Sum(usage => usage.Cost.Output),
                usages.Sum(usage => usage.Cost.CacheRead),
                usages.Sum(usage => usage.Cost.CacheWrite)));
    }
}
