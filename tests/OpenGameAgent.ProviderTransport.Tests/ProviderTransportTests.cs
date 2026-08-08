using System.Net;
using OpenGameAgent.ProviderTransport;
using Xunit;

namespace OpenGameAgent.ProviderTransport.Tests;

public sealed class ProviderTransportTests
{
    [Fact]
    public void HeaderGuardEnforcesCountNameAndValueBounds()
    {
        ProviderHeaderGuard.Validate(
            new Dictionary<string, string> { ["x-safe"] = "value" },
            "headers");

        Assert.Throws<ArgumentException>(() => ProviderHeaderGuard.Validate(
            Enumerable.Range(0, 65).Select(index => new KeyValuePair<string, string>("x-" + index, "value")),
            "headers"));
        Assert.Throws<ArgumentException>(() => ProviderHeaderGuard.Validate(
            new Dictionary<string, string> { ["bad header"] = "value" },
            "headers"));
        Assert.Throws<ArgumentException>(() => ProviderHeaderGuard.Validate(
            new Dictionary<string, string> { ["x-safe"] = "value\r\nforged" },
            "headers"));
        Assert.Throws<ArgumentException>(() => ProviderHeaderGuard.Validate(
            new Dictionary<string, string> { ["x-safe"] = new string('v', 65_537) },
            "headers"));
        Assert.Throws<ArgumentException>(() => ProviderHeaderGuard.Validate(
            new Dictionary<string, string> { ["Host"] = "example.test" },
            "headers"));
        Assert.Throws<ArgumentException>(() => ProviderHeaderGuard.Validate(
            new Dictionary<string, string> { ["Content-Length"] = "1" },
            "headers"));
        Assert.Throws<ArgumentException>(() => ProviderHeaderGuard.Validate(
            new Dictionary<string, string> { ["Transfer-Encoding"] = "chunked" },
            "headers"));
        Assert.Throws<ArgumentException>(() => ProviderHeaderGuard.Validate(
            new Dictionary<string, string> { ["Sec-WebSocket-Key"] = "secret" },
            "headers"));

        ProviderHeaderGuard.ValidateMerge(
            new Dictionary<string, string?> { ["x-optional"] = null },
            "headers");
    }

    [Fact]
    public void ObservationOnlyIncludesBoundedAllowlistedResponseMetadata()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("x-request-id", "request-1\r\nforged");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-tokens", new string('7', 2_000));
        response.Headers.TryAddWithoutValidation("set-cookie", "credential=secret");
        response.Headers.TryAddWithoutValidation("authorization", "Bearer secret");
        response.Headers.TryAddWithoutValidation("x-private-gateway", "internal");

        var observation = ProviderResponseObservation.FromHttpResponse(
            "provider",
            "api",
            "model",
            response);

        Assert.Equal(429, observation.StatusCode);
        Assert.Equal("request-1  forged", observation.Metadata["x-request-id"]);
        Assert.Equal(1_024, observation.Metadata["x-ratelimit-remaining-tokens"].Length);
        Assert.DoesNotContain(observation.Metadata.Keys, key => key.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(observation.Metadata.Values, value => value.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain("x-private-gateway", observation.Metadata.Keys);
    }

    [Fact]
    public void ProviderObservationOnlyExposesBoundedRequestId()
    {
        var observation = ProviderResponseObservation.FromProviderResponse(
            "amazon-bedrock",
            "bedrock-converse-stream",
            "model",
            503,
            new string('r', 2_000));

        Assert.Equal(1_024, observation.Metadata["request-id"].Length);
    }

    [Fact]
    public void ResponseMetadataObservationFiltersArbitraryWebSocketHeaders()
    {
        var observation = ProviderResponseObservation.FromResponseMetadata(
            "openai",
            "openai-responses",
            "model",
            101,
            new Dictionary<string, string>
            {
                ["x-request-id"] = "request-1\r\nforged",
                ["set-cookie"] = "secret=value",
                ["authorization"] = "Bearer secret",
            });

        Assert.Equal("request-1  forged", observation.Metadata["x-request-id"]);
        Assert.Single(observation.Metadata);
    }

    [Fact]
    public void HostileResponseMetadataEnumeratorIsIsolated()
    {
        var observation = ProviderResponseObservation.FromResponseMetadata(
            "provider",
            "api",
            "model",
            101,
            new ThrowingHeaders());

        Assert.Empty(observation.Metadata);
    }

    [Fact]
    public async Task ObserverFailureIsIsolated()
    {
        var observation = ProviderResponseObservation.FromProviderResponse("provider", "api", "model", 200);

        var outcome = await ProviderResponseObserverRunner.NotifyAsync(
            (_, _) => throw new InvalidOperationException("credential should not escape"),
            observation,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ProviderResponseObserverOutcome.Failed, outcome);
    }

    [Fact]
    public async Task ObserverTimeoutCancelsAndReturnsWithoutWaitingForBadCallback()
    {
        var observation = ProviderResponseObservation.FromProviderResponse("provider", "api", "model", 200);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var outcome = await ProviderResponseObserverRunner.NotifyAsync(
            async (_, token) =>
            {
                token.Register(() => cancellationSeen.TrySetResult(null));
                await release.Task.ConfigureAwait(false);
            },
            observation,
            timeoutMilliseconds: 20,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ProviderResponseObserverOutcome.TimedOut, outcome);
        await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        release.TrySetResult(null);
    }

    [Fact]
    public async Task CallerCancellationInterruptsObserver()
    {
        var observation = ProviderResponseObservation.FromProviderResponse("provider", "api", "model", 200);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ProviderResponseObserverRunner.NotifyAsync(
                async (_, token) => await Task.Delay(Timeout.Infinite, token),
                observation,
                timeoutMilliseconds: 10_000,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task PermanentObserverIsInvokedOnceAndThenSuppressed()
    {
        var observation = ProviderResponseObservation.FromProviderResponse("provider", "api", "model", 200);
        var invoked = 0;
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderResponseObserver observer = async (_, _) =>
        {
            try
            {
                Interlocked.Increment(ref invoked);
                await release.Task.ConfigureAwait(false);
            }
            finally
            {
                completed.TrySetResult(null);
            }
        };

        Assert.Equal(
            ProviderResponseObserverOutcome.TimedOut,
            await ProviderResponseObserverRunner.NotifyAsync(
                observer,
                observation,
                5,
                TestContext.Current.CancellationToken));
        for (var index = 0; index < 1_000; index++)
        {
            Assert.Equal(
                ProviderResponseObserverOutcome.Suppressed,
                await ProviderResponseObserverRunner.NotifyAsync(
                    observer,
                    observation,
                    5,
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, Volatile.Read(ref invoked));
        release.TrySetResult(null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DistinctPermanentObserversHaveAGlobalInflightBound()
    {
        var observation = ProviderResponseObservation.FromProviderResponse("provider", "api", "model", 200);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedCount = 0;
        var observers = Enumerable.Range(0, ProviderResponseObserverRunner.MaximumConcurrentObservers + 1)
            .Select(index => (ProviderResponseObserver)(async (_, _) =>
            {
                _ = index;
                try
                {
                    await release.Task.ConfigureAwait(false);
                }
                finally
                {
                    if (Interlocked.Increment(ref completedCount)
                        == ProviderResponseObserverRunner.MaximumConcurrentObservers)
                    {
                        completed.TrySetResult(null);
                    }
                }
            }))
            .ToArray();
        var outcomes = new List<ProviderResponseObserverOutcome>();

        foreach (var observer in observers)
        {
            outcomes.Add(await ProviderResponseObserverRunner.NotifyAsync(
                observer,
                observation,
                5,
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(ProviderResponseObserverRunner.MaximumConcurrentObservers, outcomes.Count(value =>
            value == ProviderResponseObserverOutcome.TimedOut));
        Assert.Single(outcomes, value => value == ProviderResponseObserverOutcome.Suppressed);
        release.TrySetResult(null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompletedObserverCanBeReusedWithoutCallerTokenRegistrationsAccumulating()
    {
        var observation = ProviderResponseObservation.FromProviderResponse("provider", "api", "model", 200);
        var invoked = 0;
        ProviderResponseObserver observer = (_, _) =>
        {
            Interlocked.Increment(ref invoked);
            return ValueTask.CompletedTask;
        };
        using var caller = new CancellationTokenSource();

        for (var index = 0; index < 1_000; index++)
        {
            Assert.Equal(
                ProviderResponseObserverOutcome.Completed,
                await ProviderResponseObserverRunner.NotifyAsync(observer, observation, cancellationToken: caller.Token));
        }

        caller.Cancel();
        Assert.Equal(1_000, Volatile.Read(ref invoked));
    }

    [Fact]
    public async Task CallbackCancellationReturnsBeforeNonCooperativeCallbackAndObservesLateFault()
    {
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var operation = ProviderCallbackRunner.RunAsync<string>(
            async _ =>
            {
                await release.Task.ConfigureAwait(false);
                throw new InvalidOperationException("late");
            },
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        release.TrySetResult(null);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(409, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(400, false)]
    public void RetryMetadataClassifiesStatus(int statusCode, bool expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)statusCode);

        Assert.Equal(expected, ProviderHttpRetryMetadata.FromResponse(response).IsTransient);
    }

    [Fact]
    public void RetryDirectiveOverridesStatusAndRetryAfterDateIsParsed()
    {
        var now = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
        using var retry = new HttpResponseMessage(HttpStatusCode.BadRequest);
        retry.Headers.TryAddWithoutValidation("x-should-retry", "true");
        retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(3));
        using var noRetry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        noRetry.Headers.TryAddWithoutValidation("x-should-retry", "false");

        var metadata = ProviderHttpRetryMetadata.FromResponse(retry, now);

        Assert.True(metadata.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(3), metadata.RetryAfter);
        Assert.False(ProviderHttpRetryMetadata.FromResponse(noRetry).IsTransient);
    }

    [Fact]
    public void RetryAfterMillisecondsIsClampedAndProviderDecisionCanOverrideStatus()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryAddWithoutValidation("retry-after-ms", "1e100");

        var responseMetadata = ProviderHttpRetryMetadata.FromResponse(response);
        Assert.Equal(TimeSpan.MaxValue, responseMetadata.RetryAfter);
        Assert.False(responseMetadata.IsTransient);
        Assert.False(ProviderHttpRetryMetadata.FromStatus(503, providerRetryable: false).IsTransient);
        Assert.True(ProviderHttpRetryMetadata.FromStatus(null).IsTransient);
    }

    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("Monthly usage limit reached; enable available balance")]
    [InlineData("billing account disabled")]
    public void QuotaAndBillingRateLimitsAreTerminal(string errorText)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("x-should-retry", "true");

        Assert.False(ProviderHttpRetryMetadata.FromResponse(response, errorText: errorText).IsTransient);
    }

    [Fact]
    public void OrdinaryRateLimitRemainsTransient()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.True(ProviderHttpRetryMetadata.FromResponse(response, errorText: "rate limit exceeded").IsTransient);
    }

    private sealed class ThrowingHeaders : IReadOnlyDictionary<string, string>
    {
        public int Count => 1;

        public IEnumerable<string> Keys => throw new InvalidOperationException("hostile metadata");

        public IEnumerable<string> Values => throw new InvalidOperationException("hostile metadata");

        public string this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            throw new InvalidOperationException("hostile metadata");

        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
