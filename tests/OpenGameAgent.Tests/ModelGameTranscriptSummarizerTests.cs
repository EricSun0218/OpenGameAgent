using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class ModelGameTranscriptSummarizerTests
{
    [Fact]
    public async Task UsesOneZeroToolCoordinateFreeRequestAndProjectsOnlyVisibleBoundedHistory()
    {
        var provider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[]
            {
                new ReasoningContent("summary reasoning must remain private"),
                new TextContent("safe compact summary"),
            },
            ModelStopReason.Stop,
            new ModelUsage(20, 5),
            provider: "provider-a",
            api: "api-a",
            responseModel: "served-model"));
        var summarizer = new ModelGameTranscriptSummarizer(provider, "summary-model");
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);
        var call = new ToolCallContent(
            "opaque-call-id",
            "inspect_state",
            "{\"secret_argument\":\"must-not-replay\"}");
        var messages = new AgentMessage[]
        {
            AgentMessage.User("question"),
            new(
                AgentRole.Assistant,
                new AgentContent[]
                {
                    new ReasoningContent("hidden-plan", "opaque-signature"),
                    new TextContent("I will inspect."),
                    call,
                },
                DateTimeOffset.UnixEpoch,
                model: "main-model",
                stopReason: ModelStopReason.ToolUse),
            AgentMessage.ToolResult(
                call,
                new ToolResult(new AgentContent[]
                {
                    new TextContent("visible outcome"),
                    new ResourceContent("game://private/resource", "application/json"),
                }),
                DateTimeOffset.UnixEpoch),
            new(
                AgentRole.Assistant,
                new AgentContent[] { new TextContent("finished") },
                DateTimeOffset.UnixEpoch,
                model: "main-model",
                stopReason: ModelStopReason.Stop),
        };

        var result = await compactor.CompactAsync(
            new GameTranscriptCompactionContext(
                new GameSessionKey("canonical-session", "canonical-actor"),
                messages,
                targetMessageCount: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal("safe compact summary", Assert.IsType<TextContent>(Assert.Single(result.Messages[0].Content)).Text);
        Assert.Equal(25, result.Usage.TotalTokens);
        var request = Assert.Single(provider.Requests);
        Assert.Empty(request.Tools);
        Assert.Null(request.SessionId);
        Assert.Equal("summary-model", request.Model);
        Assert.Equal(8_192, request.Parameters.MaxOutputTokens);
        Assert.Equal(ModelCacheRetention.None, request.Parameters.CacheRetention);
        var input = Assert.IsType<JsonContent>(Assert.Single(Assert.Single(request.Messages).Content)).Json;
        Assert.Contains("visible outcome", input, StringComparison.Ordinal);
        Assert.Contains("inspect_state", input, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical-session", input, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical-actor", input, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-call-id", input, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_argument", input, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-plan", input, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-signature", input, StringComparison.Ordinal);
        Assert.DoesNotContain("game://private/resource", input, StringComparison.Ordinal);
        Assert.Contains("resource omitted", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsTruncatedSummaryAndPreservesAttemptUsage()
    {
        var usage = new ModelUsage(10, 4);
        var provider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new TextContent("partial") },
            ModelStopReason.Length,
            usage,
            provider: "provider-a",
            api: "api-a",
            responseModel: "served-model"));
        var summarizer = new ModelGameTranscriptSummarizer(provider, "summary-model");
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(compactor));

        Assert.Equal(usage.TotalTokens, exception.Usage.TotalTokens);
        var attempt = Assert.Single(exception.Details.SummaryAttempts);
        Assert.False(attempt.Succeeded);
        Assert.True(attempt.Retryable);
        Assert.Contains("truncated", attempt.Error, StringComparison.OrdinalIgnoreCase);
        using var details = JsonDocument.Parse(attempt.DetailsJson!);
        Assert.Equal("summary_truncated", details.RootElement.GetProperty("code").GetString());
        Assert.Equal("Length", details.RootElement.GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task RejectsToolOutputEvenThoughNoToolsWereAdvertised()
    {
        var provider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new ToolCallContent("call", "unexpected", "{}") },
            ModelStopReason.ToolUse));
        var summarizer = new ModelGameTranscriptSummarizer(provider, "summary-model");
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(compactor));

        Assert.Empty(Assert.Single(provider.Requests).Tools);
        Assert.Contains("tool", Assert.Single(exception.Details.SummaryAttempts).Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsOversizedInputBeforeCallingTheProvider()
    {
        var provider = new RecordingProvider(_ => throw new InvalidOperationException("must not run"));
        var summarizer = new ModelGameTranscriptSummarizer(
            provider,
            "summary-model",
            new ModelGameTranscriptSummarizerOptions { MaximumInputCharacters = 32 });
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(compactor));

        Assert.Empty(provider.Requests);
        Assert.Contains("character limit", Assert.Single(exception.Details.SummaryAttempts).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsTooManySourceContentPartsBeforeCallingTheProvider()
    {
        var provider = new RecordingProvider(_ => throw new InvalidOperationException("must not run"));
        var summarizer = new ModelGameTranscriptSummarizer(
            provider,
            "summary-model",
            new ModelGameTranscriptSummarizerOptions { MaximumContentPartsPerMessage = 1 });
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);
        var messages = new[]
        {
            new AgentMessage(
                AgentRole.User,
                new AgentContent[] { new TextContent("one"), new TextContent("two") },
                DateTimeOffset.UnixEpoch),
            Assistant("answer"),
        };

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await compactor.CompactAsync(
                new GameTranscriptCompactionContext(new GameSessionKey("session", "actor"), messages, 1),
                TestContext.Current.CancellationToken));

        Assert.Empty(provider.Requests);
        Assert.Contains("content-part limit", Assert.Single(exception.Details.SummaryAttempts).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsTooManyOrOversizedResponsePartsWithoutJoiningThem()
    {
        var excessiveParts = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new TextContent("one"), new TextContent("two") },
            ModelStopReason.Stop));
        var partLimited = new ModelGameTranscriptSummarizer(
            excessiveParts,
            "summary-model",
            new ModelGameTranscriptSummarizerOptions { MaximumContentPartsPerMessage = 1 });
        var partException = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(new SummarizingGameTranscriptCompactor(partLimited.SummarizeAsync, 1)));

        Assert.Contains("unsupported output", Assert.Single(partException.Details.SummaryAttempts).Error!, StringComparison.Ordinal);

        var excessiveText = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new TextContent("123"), new TextContent("456") },
            ModelStopReason.Stop));
        var characterLimited = new ModelGameTranscriptSummarizer(
            excessiveText,
            "summary-model",
            new ModelGameTranscriptSummarizerOptions { MaximumSummaryCharacters = 5 });
        var textException = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(new SummarizingGameTranscriptCompactor(characterLimited.SummarizeAsync, 1)));

        Assert.Contains("character limit", Assert.Single(textException.Details.SummaryAttempts).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidProviderStreamFailsWithoutIncludingProviderText()
    {
        var provider = new RecordingProvider(_ => null);
        var summarizer = new ModelGameTranscriptSummarizer(provider, "summary-model");
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(compactor));

        Assert.Contains("invalid stream", Assert.Single(exception.Details.SummaryAttempts).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedProviderFailureIsSanitized()
    {
        var provider = new ThrowingProvider("secret provider response");
        var summarizer = new ModelGameTranscriptSummarizer(provider, "summary-model");
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(compactor));

        var attempt = Assert.Single(exception.Details.SummaryAttempts);
        Assert.Equal("The transcript summary provider request failed.", attempt.Error);
        Assert.DoesNotContain("secret", attempt.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", attempt.DetailsJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderPreflightRunsBeforeStreamingAndFailsClosed()
    {
        var provider = new PreflightFailingProvider();
        var summarizer = new ModelGameTranscriptSummarizer(provider, "summary-model");
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(compactor));

        Assert.Equal(1, provider.PreflightCalls);
        Assert.Equal(0, provider.StreamCalls);
        Assert.Equal(
            "The transcript summary provider rejected request preflight.",
            Assert.Single(exception.Details.SummaryAttempts).Error);
    }

    [Fact]
    public async Task SummaryRequestsAlwaysDisableCacheAndDeferredExecution()
    {
        var provider = new RecordingProvider(_ => new ModelResponse(
            new AgentContent[] { new TextContent("summary") },
            ModelStopReason.Stop));
        var summarizer = new ModelGameTranscriptSummarizer(
            provider,
            "summary-model",
            new ModelGameTranscriptSummarizerOptions
            {
                Parameters = new ModelParameters
                {
                    CacheRetention = ModelCacheRetention.Long,
                    Deferred = true,
                    DeferredWindow = ModelDeferredWindow.OneHour,
                },
            });
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        await CompactAsync(compactor);

        var parameters = Assert.Single(provider.Requests).Parameters;
        Assert.Equal(ModelCacheRetention.None, parameters.CacheRetention);
        Assert.False(parameters.Deferred);
        Assert.Null(parameters.DeferredWindow);
        Assert.Equal(8_192, parameters.MaxOutputTokens);
    }

    [Fact]
    public async Task TimesOutAnUnresponsiveProviderWithoutCancellingTheCaller()
    {
        var provider = new WaitingProvider();
        var summarizer = new ModelGameTranscriptSummarizer(
            provider,
            "summary-model",
            new ModelGameTranscriptSummarizerOptions { TimeoutMilliseconds = 25 });
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(compactor));

        var attempt = Assert.Single(exception.Details.SummaryAttempts);
        Assert.True(attempt.Retryable);
        Assert.Equal("The transcript summary provider request timed out.", attempt.Error);
    }

    [Fact]
    public async Task RejectsAnUnboundedProviderEventStream()
    {
        var provider = new ExcessEventProvider();
        var summarizer = new ModelGameTranscriptSummarizer(
            provider,
            "summary-model",
            new ModelGameTranscriptSummarizerOptions { MaximumStreamEvents = 2 });
        var compactor = new SummarizingGameTranscriptCompactor(summarizer.SummarizeAsync, maxSummaryAttempts: 1);

        var exception = await Assert.ThrowsAsync<GameTranscriptCompactionException>(async () =>
            await CompactAsync(compactor));

        Assert.Equal(
            "The transcript summary provider returned an invalid stream.",
            Assert.Single(exception.Details.SummaryAttempts).Error);
    }

    private static ValueTask<GameTranscriptCompactionResult> CompactAsync(
        SummarizingGameTranscriptCompactor compactor) =>
        compactor.CompactAsync(
            new GameTranscriptCompactionContext(
                new GameSessionKey("session", "actor"),
                new[] { AgentMessage.User("old"), Assistant("answer") },
                targetMessageCount: 1),
            TestContext.Current.CancellationToken);

    private static AgentMessage Assistant(string text) => new(
        AgentRole.Assistant,
        new AgentContent[] { new TextContent(text) },
        DateTimeOffset.UnixEpoch,
        model: "main-model",
        stopReason: ModelStopReason.Stop);

    private sealed class RecordingProvider : IModelProvider
    {
        private readonly Func<ModelRequest, ModelResponse?> _response;

        public RecordingProvider(Func<ModelRequest, ModelResponse?> response)
        {
            _response = response;
        }

        public List<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var response = _response(request);
            if (response is not null)
            {
                yield return ModelStreamEvent.Terminal(response);
            }
        }
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        private readonly string _message;

        public ThrowingProvider(string message)
        {
            _message = message;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new Exception(_message);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class PreflightFailingProvider : IModelProvider, IModelRequestPreflight
    {
        public int PreflightCalls { get; private set; }

        public int StreamCalls { get; private set; }

        public ValueTask ValidateRequestAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            PreflightCalls++;
            throw new InvalidDataException("secret preflight details");
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamCalls++;
            await Task.Yield();
            yield break;
        }
    }

    private sealed class WaitingProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class ExcessEventProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var partial = new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending);
            yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, partial);
            yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, partial);
            yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, partial);
        }
    }
}
