namespace GameAgent.Core;

/// <summary>
/// Controls whether a provider failure stops the request, moves directly to
/// the next route, or retries the current route before moving on.
/// </summary>
public enum ProviderFailureDisposition
{
    AbortRun = 0,
    Failover = 1,
    RetryThenFailover = 2
}

/// <summary>
/// Process-local route health policy shared by every run handled by one
/// <see cref="ProviderAttemptRunner"/>.
/// </summary>
public sealed class ProviderRouteResilienceOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan InitialCooldown { get; set; } =
        TimeSpan.FromSeconds(15);

    public TimeSpan MaxCooldown { get; set; } =
        TimeSpan.FromMinutes(2);

    public int MaxTrackedRoutes { get; set; } = 256;

    internal ProviderRouteResilienceOptions Snapshot()
    {
        if (InitialCooldown <= TimeSpan.Zero
            || InitialCooldown > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialCooldown),
                "The initial provider-route cooldown must be positive and no longer than 24 hours.");
        }

        if (MaxCooldown < InitialCooldown
            || MaxCooldown > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCooldown),
                "The maximum provider-route cooldown must be at least the initial cooldown and no longer than 24 hours.");
        }

        if (MaxTrackedRoutes is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTrackedRoutes),
                "Tracked provider routes must be between 1 and 4096.");
        }

        return new ProviderRouteResilienceOptions
        {
            Enabled = Enabled,
            InitialCooldown = InitialCooldown,
            MaxCooldown = MaxCooldown,
            MaxTrackedRoutes = MaxTrackedRoutes
        };
    }
}

internal sealed class ProviderRouteHealthRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RouteState> _states =
        new(StringComparer.Ordinal);
    private readonly ProviderRouteResilienceOptions _options;
    private readonly IRuntimeClock _clock;
    private long _sequence;

    public ProviderRouteHealthRegistry(
        ProviderRouteResilienceOptions options,
        IRuntimeClock clock)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options)))
            .Snapshot();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ProviderRouteAdmission Acquire(string routeDigest)
    {
        routeDigest = RuntimeGuard.RequiredUtf8(
            routeDigest,
            128,
            nameof(routeDigest));
        if (!_options.Enabled)
        {
            return ProviderRouteAdmission.AdmittedHealthy(this, routeDigest);
        }

        var now = _clock.UtcNow;
        lock (_sync)
        {
            if (!_states.TryGetValue(routeDigest, out var state))
            {
                return ProviderRouteAdmission.AdmittedHealthy(
                    this,
                    routeDigest);
            }

            state.LastTouched = NextSequence();
            if (state.ProbeInFlight)
            {
                return ProviderRouteAdmission.Rejected(
                    this,
                    routeDigest,
                    ProviderRouteAdmissionRejection.ProbeInProgress);
            }

            if (now < state.OpenUntil)
            {
                return ProviderRouteAdmission.Rejected(
                    this,
                    routeDigest,
                    ProviderRouteAdmissionRejection.CoolingDown);
            }

            state.ProbeInFlight = true;
            return ProviderRouteAdmission.AdmittedProbe(
                this,
                routeDigest,
                state.Generation);
        }
    }

    public void ReportSuccess(ProviderRouteAdmission admission)
    {
        if (!_options.Enabled || !admission.IsHalfOpenProbe)
        {
            return;
        }

        lock (_sync)
        {
            if (_states.TryGetValue(admission.RouteDigest, out var state)
                && state.Generation == admission.Generation
                && state.ProbeInFlight)
            {
                _states.Remove(admission.RouteDigest);
            }
        }
    }

    public void ReportRouteFailure(ProviderRouteAdmission admission)
    {
        if (!_options.Enabled || !admission.IsAdmitted)
        {
            return;
        }

        var now = _clock.UtcNow;
        lock (_sync)
        {
            if (admission.IsHalfOpenProbe)
            {
                if (!_states.TryGetValue(
                        admission.RouteDigest,
                        out var probeState)
                    || probeState.Generation != admission.Generation
                    || !probeState.ProbeInFlight)
                {
                    return;
                }

                Open(
                    probeState,
                    checked(probeState.ConsecutiveFailures + 1),
                    now);
                return;
            }

            if (_states.ContainsKey(admission.RouteDigest))
            {
                // This attempt was admitted while the route was healthy. A
                // concurrent failure has already opened a newer generation,
                // so do not multiply cooldown because of the same in-flight
                // wave.
                return;
            }

            if (_states.Count >= _options.MaxTrackedRoutes
                && !EvictOldestIdleState())
            {
                return;
            }

            var state = new RouteState();
            _states.Add(admission.RouteDigest, state);
            Open(state, 1, now);
        }
    }

    public void Release(ProviderRouteAdmission admission)
    {
        if (!_options.Enabled || !admission.IsHalfOpenProbe)
        {
            return;
        }

        lock (_sync)
        {
            if (_states.TryGetValue(admission.RouteDigest, out var state)
                && state.Generation == admission.Generation
                && state.ProbeInFlight)
            {
                state.ProbeInFlight = false;
                state.LastTouched = NextSequence();
            }
        }
    }

    private void Open(
        RouteState state,
        int consecutiveFailures,
        DateTimeOffset now)
    {
        state.ConsecutiveFailures = consecutiveFailures;
        state.Generation = checked(state.Generation + 1);
        state.ProbeInFlight = false;
        state.LastTouched = NextSequence();
        state.OpenUntil = AddClamped(
            now,
            CooldownFor(consecutiveFailures));
    }

    private TimeSpan CooldownFor(int consecutiveFailures)
    {
        var ticks = _options.InitialCooldown.Ticks;
        var maximumTicks = _options.MaxCooldown.Ticks;
        for (var failure = 1;
             failure < consecutiveFailures && ticks < maximumTicks;
             failure++)
        {
            ticks = ticks > maximumTicks / 2
                ? maximumTicks
                : Math.Min(maximumTicks, checked(ticks * 2));
        }

        return TimeSpan.FromTicks(ticks);
    }

    private bool EvictOldestIdleState()
    {
        string? candidateKey = null;
        var candidateSequence = long.MaxValue;
        foreach (var pair in _states)
        {
            if (!pair.Value.ProbeInFlight
                && pair.Value.LastTouched < candidateSequence)
            {
                candidateKey = pair.Key;
                candidateSequence = pair.Value.LastTouched;
            }
        }

        return candidateKey is not null && _states.Remove(candidateKey);
    }

    private long NextSequence()
    {
        if (_sequence == long.MaxValue)
        {
            _sequence = 0;
            foreach (var state in _states.Values)
            {
                state.LastTouched = 0;
            }
        }

        return ++_sequence;
    }

    private static DateTimeOffset AddClamped(
        DateTimeOffset value,
        TimeSpan duration)
    {
        var remainingTicks = DateTimeOffset.MaxValue.UtcTicks
                             - value.UtcTicks;
        return duration.Ticks >= remainingTicks
            ? DateTimeOffset.MaxValue
            : value.Add(duration);
    }

    private sealed class RouteState
    {
        public int ConsecutiveFailures { get; set; }

        public long Generation { get; set; }

        public bool ProbeInFlight { get; set; }

        public DateTimeOffset OpenUntil { get; set; }

        public long LastTouched { get; set; }
    }
}

internal enum ProviderRouteAdmissionRejection
{
    None = 0,
    CoolingDown = 1,
    ProbeInProgress = 2
}

internal sealed class ProviderRouteAdmission : IDisposable
{
    private readonly ProviderRouteHealthRegistry _owner;
    private int _completed;

    private ProviderRouteAdmission(
        ProviderRouteHealthRegistry owner,
        string routeDigest,
        bool isAdmitted,
        bool isHalfOpenProbe,
        long generation,
        ProviderRouteAdmissionRejection rejection)
    {
        _owner = owner;
        RouteDigest = routeDigest;
        IsAdmitted = isAdmitted;
        IsHalfOpenProbe = isHalfOpenProbe;
        Generation = generation;
        Rejection = rejection;
    }

    public string RouteDigest { get; }

    public bool IsAdmitted { get; }

    public bool IsHalfOpenProbe { get; }

    public long Generation { get; }

    public ProviderRouteAdmissionRejection Rejection { get; }

    public void ReportSuccess()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _owner.ReportSuccess(this);
        }
    }

    public void ReportRouteFailure()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _owner.ReportRouteFailure(this);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _owner.Release(this);
        }
    }

    public static ProviderRouteAdmission AdmittedHealthy(
        ProviderRouteHealthRegistry owner,
        string routeDigest)
    {
        return new ProviderRouteAdmission(
            owner,
            routeDigest,
            isAdmitted: true,
            isHalfOpenProbe: false,
            generation: 0,
            ProviderRouteAdmissionRejection.None);
    }

    public static ProviderRouteAdmission AdmittedProbe(
        ProviderRouteHealthRegistry owner,
        string routeDigest,
        long generation)
    {
        return new ProviderRouteAdmission(
            owner,
            routeDigest,
            isAdmitted: true,
            isHalfOpenProbe: true,
            generation,
            ProviderRouteAdmissionRejection.None);
    }

    public static ProviderRouteAdmission Rejected(
        ProviderRouteHealthRegistry owner,
        string routeDigest,
        ProviderRouteAdmissionRejection rejection)
    {
        return new ProviderRouteAdmission(
            owner,
            routeDigest,
            isAdmitted: false,
            isHalfOpenProbe: false,
            generation: 0,
            rejection);
    }
}
