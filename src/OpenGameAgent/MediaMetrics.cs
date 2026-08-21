using System.Collections.ObjectModel;

namespace OpenGameAgent;

public sealed class GameMediaLatencySample
{
    internal GameMediaLatencySample(
        string requestId,
        GameMediaKind kind,
        bool succeeded,
        double? firstProgressMilliseconds,
        double assetAvailableMilliseconds,
        string? errorType)
    {
        RequestId = requestId;
        Kind = kind;
        Succeeded = succeeded;
        FirstProgressMilliseconds = firstProgressMilliseconds;
        AssetAvailableMilliseconds = assetAvailableMilliseconds;
        ErrorType = errorType;
    }

    public string RequestId { get; }
    public GameMediaKind Kind { get; }
    public bool Succeeded { get; }
    public double? FirstProgressMilliseconds { get; }
    public double AssetAvailableMilliseconds { get; }
    public string? ErrorType { get; }
}

/// <summary>Optional bounded timing wrapper for any <see cref="IGameMediaGenerator"/>.</summary>
public sealed class GameMediaMetricsCollector
{
    private readonly object _gate = new();
    private readonly Queue<GameMediaLatencySample> _samples;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _clock;

    public GameMediaMetricsCollector(int capacity = 1_024, Func<DateTimeOffset>? clock = null)
    {
        if (capacity is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _samples = new Queue<GameMediaLatencySample>(Math.Min(capacity, 1_024));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<GameMediaGenerationResult> GenerateAsync(
        IGameMediaGenerator generator,
        GameMediaGenerationRequest request,
        GameMediaProgressHandler? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (generator is null)
        {
            throw new ArgumentNullException(nameof(generator));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var startedAt = _clock();
        DateTimeOffset? firstProgressAt = null;
        var progressGate = new object();
        try
        {
            var result = await generator.GenerateAsync(
                request,
                async (value, token) =>
                {
                    lock (progressGate)
                    {
                        firstProgressAt ??= _clock();
                    }

                    if (progress is not null)
                    {
                        await progress(value, token).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The media generator returned no result.");
            DateTimeOffset? observedProgress;
            lock (progressGate)
            {
                observedProgress = firstProgressAt;
            }

            Add(request, startedAt, observedProgress, _clock(), succeeded: true, errorType: null);
            return result;
        }
        catch (Exception exception)
        {
            DateTimeOffset? observedProgress;
            lock (progressGate)
            {
                observedProgress = firstProgressAt;
            }

            Add(request, startedAt, observedProgress, _clock(), succeeded: false, exception.GetType().Name);
            throw;
        }
    }

    public IReadOnlyList<GameMediaLatencySample> Snapshot()
    {
        lock (_gate)
        {
            return new ReadOnlyCollection<GameMediaLatencySample>(_samples.ToArray());
        }
    }

    private void Add(
        GameMediaGenerationRequest request,
        DateTimeOffset startedAt,
        DateTimeOffset? firstProgressAt,
        DateTimeOffset endedAt,
        bool succeeded,
        string? errorType)
    {
        var sample = new GameMediaLatencySample(
            request.RequestId,
            request.Kind,
            succeeded,
            firstProgressAt is null ? null : Math.Max(0, (firstProgressAt.Value - startedAt).TotalMilliseconds),
            Math.Max(0, (endedAt - startedAt).TotalMilliseconds),
            errorType);
        lock (_gate)
        {
            while (_samples.Count >= _capacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(sample);
        }
    }
}
