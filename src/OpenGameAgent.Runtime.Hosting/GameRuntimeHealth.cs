using System.Diagnostics;

namespace OpenGameAgent.Runtime.Hosting;

public enum GameRuntimeComponentKind
{
    Runtime,
    Provider,
    Mcp,
    LocalEndpoint,
    Realtime,
    Media,
    Extension,
    Other,
}

public enum GameRuntimeComponentState
{
    Declared,
    Available,
    Ready,
    Degraded,
    Unavailable,
}

public sealed class GameRuntimeHealthProbeResult
{
    public GameRuntimeHealthProbeResult(
        GameRuntimeComponentState state,
        string? diagnosticCode = null,
        string? detail = null)
    {
        State = state;
        DiagnosticCode = NormalizeOptional(diagnosticCode, nameof(diagnosticCode), 128);
        Detail = NormalizeOptional(detail, nameof(detail), 512);
    }

    public GameRuntimeComponentState State { get; }

    public string? DiagnosticCode { get; }

    public string? Detail { get; }

    private static string? NormalizeOptional(string? value, string parameterName, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Health diagnostics must be printable and bounded.", parameterName);
        }

        return normalized;
    }
}

public sealed class GameRuntimeComponentHealth
{
    public GameRuntimeComponentHealth(
        GameRuntimeComponentKind kind,
        string name,
        bool required,
        GameRuntimeComponentState state,
        DateTimeOffset checkedAt,
        double elapsedMilliseconds,
        string? diagnosticCode = null,
        string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
        {
            throw new ArgumentException("A printable component name of at most 128 characters is required.", nameof(name));
        }

        if (double.IsNaN(elapsedMilliseconds) || double.IsInfinity(elapsedMilliseconds) || elapsedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        }

        var result = new GameRuntimeHealthProbeResult(state, diagnosticCode, detail);
        Kind = kind;
        Name = name.Trim();
        Required = required;
        State = state;
        CheckedAt = checkedAt;
        ElapsedMilliseconds = elapsedMilliseconds;
        DiagnosticCode = result.DiagnosticCode;
        Detail = result.Detail;
    }

    public GameRuntimeComponentKind Kind { get; }

    public string Name { get; }

    public bool Required { get; }

    public GameRuntimeComponentState State { get; }

    public DateTimeOffset CheckedAt { get; }

    public double ElapsedMilliseconds { get; }

    public string? DiagnosticCode { get; }

    public string? Detail { get; }
}

public sealed class GameRuntimeHealthSnapshot
{
    public GameRuntimeHealthSnapshot(
        GameRuntimeComponentState state,
        DateTimeOffset checkedAt,
        IReadOnlyList<GameRuntimeComponentHealth> components)
    {
        State = state;
        CheckedAt = checkedAt;
        Components = components ?? throw new ArgumentNullException(nameof(components));
    }

    public GameRuntimeComponentState State { get; }

    public DateTimeOffset CheckedAt { get; }

    public IReadOnlyList<GameRuntimeComponentHealth> Components { get; }
}

public interface IGameRuntimeHealthProbe
{
    GameRuntimeComponentKind Kind { get; }

    string Name { get; }

    bool Required { get; }

    ValueTask<GameRuntimeHealthProbeResult> CheckAsync(CancellationToken cancellationToken);
}

public interface IGameRuntimeHealthMonitor
{
    ValueTask<GameRuntimeHealthSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class GameRuntimeHealthMonitorOptions
{
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int MaximumProbes { get; set; } = 128;

    public int MaximumConcurrency { get; set; } = 8;

    public bool OptionalUnavailableIsDegraded { get; set; } = true;
}

public sealed class DelegateGameRuntimeHealthProbe : IGameRuntimeHealthProbe
{
    private readonly Func<CancellationToken, ValueTask<GameRuntimeHealthProbeResult>> _check;

    public DelegateGameRuntimeHealthProbe(
        GameRuntimeComponentKind kind,
        string name,
        bool required,
        Func<CancellationToken, ValueTask<GameRuntimeHealthProbeResult>> check)
    {
        ValidateProbeIdentity(name);
        Kind = kind;
        Name = name.Trim();
        Required = required;
        _check = check ?? throw new ArgumentNullException(nameof(check));
    }

    public GameRuntimeComponentKind Kind { get; }

    public string Name { get; }

    public bool Required { get; }

    public ValueTask<GameRuntimeHealthProbeResult> CheckAsync(CancellationToken cancellationToken) =>
        _check(cancellationToken);

    internal static void ValidateProbeIdentity(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
        {
            throw new ArgumentException("A printable health-probe name of at most 128 characters is required.", nameof(name));
        }
    }
}

public sealed class StaticGameRuntimeHealthProbe : IGameRuntimeHealthProbe
{
    private readonly GameRuntimeHealthProbeResult _result;

    public StaticGameRuntimeHealthProbe(
        GameRuntimeComponentKind kind,
        string name,
        bool required,
        GameRuntimeComponentState state,
        string? diagnosticCode = null,
        string? detail = null)
    {
        DelegateGameRuntimeHealthProbe.ValidateProbeIdentity(name);
        Kind = kind;
        Name = name.Trim();
        Required = required;
        _result = new GameRuntimeHealthProbeResult(state, diagnosticCode, detail);
    }

    public GameRuntimeComponentKind Kind { get; }

    public string Name { get; }

    public bool Required { get; }

    public ValueTask<GameRuntimeHealthProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<GameRuntimeHealthProbeResult>(_result);
    }
}

public sealed class GameRuntimeHealthMonitor : IGameRuntimeHealthMonitor
{
    private readonly IReadOnlyList<IGameRuntimeHealthProbe> _probes;
    private readonly GameRuntimeHealthMonitorOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public GameRuntimeHealthMonitor(
        IEnumerable<IGameRuntimeHealthProbe> probes,
        GameRuntimeHealthMonitorOptions? options = null,
        Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? new GameRuntimeHealthMonitorOptions();
        ValidateOptions(_options);
        _probes = (probes ?? throw new ArgumentNullException(nameof(probes))).ToArray();
        if (_probes.Count > _options.MaximumProbes)
        {
            throw new ArgumentException("The configured health-probe count exceeds the monitor limit.", nameof(probes));
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var probe in _probes)
        {
            if (probe is null)
            {
                throw new ArgumentException("Health probes cannot contain null entries.", nameof(probes));
            }

            DelegateGameRuntimeHealthProbe.ValidateProbeIdentity(probe.Name);
            if (!identities.Add(probe.Kind + ":" + probe.Name.Trim()))
            {
                throw new ArgumentException("Health-probe identities must be unique.", nameof(probes));
            }
        }

        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<GameRuntimeHealthSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkedAt = _clock();
        if (_probes.Count == 0)
        {
            return new GameRuntimeHealthSnapshot(GameRuntimeComponentState.Ready, checkedAt, Array.Empty<GameRuntimeComponentHealth>());
        }

        using var concurrency = new SemaphoreSlim(_options.MaximumConcurrency, _options.MaximumConcurrency);
        var tasks = _probes.Select(probe => CheckOneAsync(probe, checkedAt, concurrency, cancellationToken)).ToArray();
        var components = await Task.WhenAll(tasks).ConfigureAwait(false);
        Array.Sort(components, static (left, right) =>
        {
            var kind = left.Kind.CompareTo(right.Kind);
            return kind != 0 ? kind : string.CompareOrdinal(left.Name, right.Name);
        });

        return new GameRuntimeHealthSnapshot(Aggregate(components), checkedAt, components);
    }

    private async Task<GameRuntimeComponentHealth> CheckOneAsync(
        IGameRuntimeHealthProbe probe,
        DateTimeOffset checkedAt,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ProbeTimeout);
            try
            {
                var result = await probe.CheckAsync(timeout.Token).ConfigureAwait(false);
                if (result is null)
                {
                    return Component(probe, GameRuntimeComponentState.Unavailable, checkedAt, stopwatch, "probe-null");
                }

                return Component(probe, result.State, checkedAt, stopwatch, result.DiagnosticCode, result.Detail);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Component(probe, GameRuntimeComponentState.Unavailable, checkedAt, stopwatch, "probe-timeout");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Component(
                    probe,
                    GameRuntimeComponentState.Unavailable,
                    checkedAt,
                    stopwatch,
                    "probe-failed",
                    exception.GetType().Name);
            }
        }
        finally
        {
            concurrency.Release();
        }
    }

    private static GameRuntimeComponentHealth Component(
        IGameRuntimeHealthProbe probe,
        GameRuntimeComponentState state,
        DateTimeOffset checkedAt,
        Stopwatch stopwatch,
        string? diagnosticCode = null,
        string? detail = null) =>
        new(
            probe.Kind,
            probe.Name,
            probe.Required,
            state,
            checkedAt,
            stopwatch.Elapsed.TotalMilliseconds,
            diagnosticCode,
            detail);

    private GameRuntimeComponentState Aggregate(IReadOnlyList<GameRuntimeComponentHealth> components)
    {
        if (components.Any(component => component.Required && component.State == GameRuntimeComponentState.Unavailable))
        {
            return GameRuntimeComponentState.Unavailable;
        }

        if (components.Any(component => component.Required && component.State != GameRuntimeComponentState.Ready)
            || components.Any(component => component.State == GameRuntimeComponentState.Degraded)
            || (_options.OptionalUnavailableIsDegraded
                && components.Any(component => !component.Required && component.State == GameRuntimeComponentState.Unavailable)))
        {
            return GameRuntimeComponentState.Degraded;
        }

        if (components.All(component => component.State == GameRuntimeComponentState.Ready))
        {
            return GameRuntimeComponentState.Ready;
        }

        return components.Any(component => component.State == GameRuntimeComponentState.Available)
            ? GameRuntimeComponentState.Available
            : GameRuntimeComponentState.Declared;
    }

    private static void ValidateOptions(GameRuntimeHealthMonitorOptions options)
    {
        if (options.ProbeTimeout <= TimeSpan.Zero || options.ProbeTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The health-probe timeout must be between zero and five minutes.");
        }

        if (options.MaximumProbes is < 1 or > 1024 || options.MaximumConcurrency is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Health-probe bounds are invalid.");
        }
    }
}
