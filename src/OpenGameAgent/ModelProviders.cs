using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public sealed class RetryingModelProvider : IModelProvider, IModelRequestPreflight
{
    private readonly IModelProvider _inner;
    private readonly int _maximumAttempts;
    private readonly Func<int, TimeSpan> _delay;
    private readonly Func<Exception, bool> _isTransient;
    private readonly TimeSpan _maximumDelay;

    public RetryingModelProvider(
        IModelProvider inner,
        int maximumAttempts = 3,
        Func<int, TimeSpan>? delay = null,
        Func<Exception, bool>? isTransient = null,
        TimeSpan? maximumDelay = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (maximumAttempts < 1 || maximumAttempts > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        _maximumAttempts = maximumAttempts;
        _delay = delay ?? (attempt => TimeSpan.FromMilliseconds(Math.Min(5_000, 200 * Math.Pow(2, attempt - 1))));
        _isTransient = isTransient ?? (exception =>
            exception is not ModelProviderException providerFailure || providerFailure.IsTransient);
        _maximumDelay = maximumDelay ?? TimeSpan.FromSeconds(30);
        if (_maximumDelay < TimeSpan.Zero || _maximumDelay > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }
    }

    public ValueTask ValidateRequestAsync(ModelRequest request, CancellationToken cancellationToken) =>
        _inner is IModelRequestPreflight preflight
            ? preflight.ValidateRequestAsync(request, cancellationToken)
            : default;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var retryDelayMilliseconds = 0d;
        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            var enumerator = _inner.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var emittedMeaningfulEvent = false;
            ModelStreamEvent? pendingStart = null;
            Exception? retryFailure = null;
            Exception? primaryFailure = null;
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
                        if (attempt >= _maximumAttempts || emittedMeaningfulEvent || !_isTransient(exception))
                        {
                            throw;
                        }

                        retryFailure = exception;
                        break;
                    }

                    if (!hasNext)
                    {
                        if (emittedMeaningfulEvent)
                        {
                            yield break;
                        }

                        if (attempt >= _maximumAttempts)
                        {
                            primaryFailure = new InvalidOperationException("The model provider completed without emitting a terminal event.");
                            throw primaryFailure;
                        }

                        break;
                    }

                    var current = enumerator.Current;
                    if (current is null)
                    {
                        primaryFailure = new InvalidOperationException("The model provider emitted a null stream event.");
                        throw primaryFailure;
                    }

                    if (current.Kind == ModelStreamEventKind.Started)
                    {
                        if (pendingStart is not null)
                        {
                            primaryFailure = new InvalidOperationException("The model provider emitted more than one stream start event.");
                            throw primaryFailure;
                        }

                        pendingStart = current;
                        if (ModelProviderRetrySafety.HasMeaningfulStart(current))
                        {
                            emittedMeaningfulEvent = true;
                            yield return pendingStart;
                            pendingStart = null;
                        }

                        continue;
                    }

                    emittedMeaningfulEvent = true;
                    if (pendingStart is not null)
                    {
                        yield return pendingStart;
                        pendingStart = null;
                    }

                    if (current.IsTerminal)
                    {
                        terminalSeen = true;
                        yield return attempt == 1
                            ? current
                            : ModelProviderRetrySafety.AppendDiagnostic(
                                current,
                                "oga.provider.retry",
                                "The model request completed after a transient retry.",
                                JsonSerializer.Serialize(new
                                {
                                    attempts = attempt,
                                    retries = attempt - 1,
                                    retryDelayMilliseconds,
                                }));
                        yield break;
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
                catch when (primaryFailure is not null || retryFailure is not null || terminalSeen || cancellationToken.IsCancellationRequested)
                {
                    // Cleanup cannot replace the primary stream outcome.
                }
            }

            var wait = retryFailure is ModelProviderException { RetryAfter: { } serverDelay }
                ? serverDelay
                : _delay(attempt);
            if (wait < TimeSpan.Zero)
            {
                throw new InvalidOperationException("The retry delay cannot be negative.");
            }

            wait = wait > _maximumDelay ? _maximumDelay : wait;
            retryDelayMilliseconds += wait.TotalMilliseconds;
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class FallbackModelProvider : IModelProvider, IModelRequestPreflight
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly Func<Exception, bool> _canFallback;

    public FallbackModelProvider(
        IEnumerable<IModelProvider> providers,
        Func<Exception, bool>? canFallback = null)
    {
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        var copied = new List<IModelProvider>(providers);
        if (copied.Count == 0 || copied.Exists(provider => provider is null))
        {
            throw new ArgumentException("At least one non-null model provider is required.", nameof(providers));
        }

        _providers = new ReadOnlyCollection<IModelProvider>(copied);
        _canFallback = canFallback ?? (exception =>
            exception is not ModelProviderException providerFailure || providerFailure.IsTransient);
    }

    public async ValueTask ValidateRequestAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            if (provider is IModelRequestPreflight preflight)
            {
                await preflight.ValidateRequestAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < _providers.Count; index++)
        {
            var enumerator = _providers[index].StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var emittedMeaningfulEvent = false;
            ModelStreamEvent? pendingStart = null;
            Exception? primaryFailure = null;
            Exception? fallbackFailure = null;
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
                        if (index >= _providers.Count - 1 || emittedMeaningfulEvent || !_canFallback(exception))
                        {
                            throw;
                        }

                        fallbackFailure = exception;
                        break;
                    }

                    if (!hasNext)
                    {
                        if (emittedMeaningfulEvent)
                        {
                            yield break;
                        }

                        if (index >= _providers.Count - 1)
                        {
                            primaryFailure = new InvalidOperationException("Every fallback model provider completed without output.");
                            throw primaryFailure;
                        }

                        break;
                    }

                    var current = enumerator.Current;
                    if (current is null)
                    {
                        primaryFailure = new InvalidOperationException("A fallback model provider emitted a null stream event.");
                        throw primaryFailure;
                    }

                    if (current.Kind == ModelStreamEventKind.Started)
                    {
                        if (pendingStart is not null)
                        {
                            primaryFailure = new InvalidOperationException("A fallback model provider emitted more than one stream start event.");
                            throw primaryFailure;
                        }

                        pendingStart = current;
                        if (ModelProviderRetrySafety.HasMeaningfulStart(current))
                        {
                            emittedMeaningfulEvent = true;
                            yield return pendingStart;
                            pendingStart = null;
                        }

                        continue;
                    }

                    emittedMeaningfulEvent = true;
                    if (pendingStart is not null)
                    {
                        yield return pendingStart;
                        pendingStart = null;
                    }

                    if (current.IsTerminal)
                    {
                        terminalSeen = true;
                        yield return index == 0
                            ? current
                            : ModelProviderRetrySafety.AppendDiagnostic(
                                current,
                                "oga.provider.fallback",
                                "The model request completed through a configured fallback provider.",
                                JsonSerializer.Serialize(new
                                {
                                    providerIndex = index,
                                    fallbacks = index,
                                }));
                        yield break;
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
                catch when (primaryFailure is not null || fallbackFailure is not null || terminalSeen || cancellationToken.IsCancellationRequested)
                {
                    // Cleanup cannot replace the primary stream outcome.
                }
            }
        }
    }
}

internal static class ModelProviderRetrySafety
{
    public static bool HasMeaningfulStart(ModelStreamEvent streamEvent) =>
        streamEvent.Partial is { } partial
        && (partial.Content.Count > 0 || partial.Usage.TotalTokens > 0);

    public static ModelStreamEvent AppendDiagnostic(
        ModelStreamEvent streamEvent,
        string code,
        string message,
        string dataJson)
    {
        var response = streamEvent.Response
            ?? throw new InvalidOperationException("A terminal model event requires a response.");
        var diagnostics = response.Diagnostics
            .Concat(new[] { new ModelDiagnostic(code, message, dataJson: dataJson) })
            .ToArray();
        return ModelStreamEvent.Terminal(new ModelResponse(
            response.Content,
            response.StopReason,
            response.Usage,
            response.ErrorMessage,
            response.Provider,
            response.Api,
            response.ResponseModel,
            response.ResponseId,
            response.RawStopReason,
            response.EndTurn,
            diagnostics,
            response.Deferred));
    }
}
