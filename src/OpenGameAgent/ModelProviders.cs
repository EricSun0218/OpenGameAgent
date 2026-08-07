using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public sealed class RetryingModelProvider : IModelProvider
{
    private readonly IModelProvider _inner;
    private readonly int _maximumAttempts;
    private readonly Func<int, TimeSpan> _delay;
    private readonly Func<Exception, bool> _isTransient;

    public RetryingModelProvider(
        IModelProvider inner,
        int maximumAttempts = 3,
        Func<int, TimeSpan>? delay = null,
        Func<Exception, bool>? isTransient = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (maximumAttempts < 1 || maximumAttempts > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        _maximumAttempts = maximumAttempts;
        _delay = delay ?? (attempt => TimeSpan.FromMilliseconds(Math.Min(5_000, 200 * Math.Pow(2, attempt - 1))));
        _isTransient = isTransient ?? (_ => true);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            var enumerator = _inner.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var emittedMeaningfulEvent = false;
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
                        if (attempt >= _maximumAttempts || emittedMeaningfulEvent || !_isTransient(exception))
                        {
                            throw;
                        }

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
                            throw new InvalidOperationException("The model provider completed without emitting a terminal event.");
                        }

                        break;
                    }

                    var current = enumerator.Current;
                    if (current is null)
                    {
                        throw new InvalidOperationException("The model provider emitted a null stream event.");
                    }

                    emittedMeaningfulEvent |= current.Kind != ModelStreamEventKind.Started;
                    yield return current;
                    if (current.IsTerminal)
                    {
                        yield break;
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            var wait = _delay(attempt);
            if (wait < TimeSpan.Zero || wait > TimeSpan.FromMinutes(5))
            {
                throw new InvalidOperationException("The retry delay must be between zero and five minutes.");
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class FallbackModelProvider : IModelProvider
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
        _canFallback = canFallback ?? (_ => true);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < _providers.Count; index++)
        {
            var enumerator = _providers[index].StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var emittedMeaningfulEvent = false;
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
                        if (index >= _providers.Count - 1 || emittedMeaningfulEvent || !_canFallback(exception))
                        {
                            throw;
                        }

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
                            throw new InvalidOperationException("Every fallback model provider completed without output.");
                        }

                        break;
                    }

                    var current = enumerator.Current;
                    if (current is null)
                    {
                        throw new InvalidOperationException("A fallback model provider emitted a null stream event.");
                    }

                    emittedMeaningfulEvent |= current.Kind != ModelStreamEventKind.Started;
                    yield return current;
                    if (current.IsTerminal)
                    {
                        yield break;
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
