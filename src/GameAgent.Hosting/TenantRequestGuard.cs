namespace GameAgent.Hosting;

public sealed class GameAgentKillSwitch
{
    private readonly int _maximumBlockedTenants;
    private readonly HashSet<string> _blockedTenants = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _allBlocked;

    public GameAgentKillSwitch(int maximumBlockedTenants = 4_096)
    {
        if (maximumBlockedTenants is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumBlockedTenants));
        _maximumBlockedTenants = maximumBlockedTenants;
    }

    public void BlockAll() { lock (_sync) _allBlocked = true; }
    public void AllowAll() { lock (_sync) _allBlocked = false; }

    public void BlockTenant(string tenantId)
    {
        Validate(tenantId);
        lock (_sync)
        {
            if (_blockedTenants.Count >= _maximumBlockedTenants && !_blockedTenants.Contains(tenantId))
            {
                throw new TenantCapacityExceededException("max_blocked_tenants", "The kill-switch tenant set is full.");
            }
            _blockedTenants.Add(tenantId);
        }
    }

    public bool AllowTenant(string tenantId)
    {
        Validate(tenantId);
        lock (_sync) return _blockedTenants.Remove(tenantId);
    }

    public void EnsureAllowed(string tenantId)
    {
        Validate(tenantId);
        lock (_sync)
        {
            if (_allBlocked || _blockedTenants.Contains(tenantId))
            {
                throw new TenantCapacityExceededException("host_kill_switch", "Agent work is disabled for this tenant.");
            }
        }
    }

    private static void Validate(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 256) throw new ArgumentException("A bounded tenant ID is required.", nameof(tenantId));
    }
}

public sealed class TenantRateLimitOptions
{
    public int MaxKnownTenants { get; set; } = 4_096;
    public double TokensPerSecond { get; set; } = 4;
    public double BurstTokens { get; set; } = 16;

    internal TenantRateLimitOptions Snapshot()
    {
        if (MaxKnownTenants is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxKnownTenants));
        if (!BoundedRate(TokensPerSecond) || !BoundedRate(BurstTokens) || BurstTokens < 1) throw new ArgumentOutOfRangeException(nameof(TokensPerSecond));
        return new TenantRateLimitOptions
        {
            MaxKnownTenants = MaxKnownTenants,
            TokensPerSecond = TokensPerSecond,
            BurstTokens = BurstTokens
        };
    }

    private static bool BoundedRate(double value) =>
        value is >= 0.000001 and <= 1_000_000 && !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed class TenantRateLimitDecision
{
    internal TenantRateLimitDecision(bool allowed, TimeSpan retryAfter)
    {
        Allowed = allowed;
        RetryAfter = retryAfter;
    }
    public bool Allowed { get; }
    public TimeSpan RetryAfter { get; }
}

public sealed class TenantRateLimiter
{
    private readonly TenantRateLimitOptions _options;
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public TenantRateLimiter(TenantRateLimitOptions? options = null)
    {
        _options = (options ?? new TenantRateLimitOptions()).Snapshot();
    }

    public TenantRateLimitDecision TryAcquire(string tenantId, DateTimeOffset now, double tokens = 1)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 256) throw new ArgumentException("A bounded tenant ID is required.", nameof(tenantId));
        if (tokens <= 0 || tokens > _options.BurstTokens || double.IsNaN(tokens) || double.IsInfinity(tokens)) throw new ArgumentOutOfRangeException(nameof(tokens));
        lock (_sync)
        {
            if (!_buckets.TryGetValue(tenantId, out var bucket))
            {
                if (_buckets.Count >= _options.MaxKnownTenants) throw new TenantCapacityExceededException("max_rate_limit_tenants", "The rate-limit tenant set is full.");
                bucket = new Bucket(_options.BurstTokens, now);
                _buckets.Add(tenantId, bucket);
            }
            var elapsed = Math.Max(0, (now - bucket.UpdatedAt).TotalSeconds);
            bucket.Tokens = Math.Min(_options.BurstTokens, bucket.Tokens + elapsed * _options.TokensPerSecond);
            if (now > bucket.UpdatedAt) bucket.UpdatedAt = now;
            if (bucket.Tokens >= tokens)
            {
                bucket.Tokens -= tokens;
                return new TenantRateLimitDecision(true, TimeSpan.Zero);
            }
            var seconds = (tokens - bucket.Tokens) / _options.TokensPerSecond;
            var retryAfter = seconds >= TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.MaxValue
                : TimeSpan.FromSeconds(seconds);
            return new TenantRateLimitDecision(false, retryAfter);
        }
    }

    public bool RemoveTenant(string tenantId)
    {
        lock (_sync) return _buckets.Remove(tenantId);
    }

    private sealed class Bucket
    {
        public Bucket(double tokens, DateTimeOffset updatedAt) { Tokens = tokens; UpdatedAt = updatedAt; }
        public double Tokens { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}

public sealed class FailureCircuitBreakerOptions
{
    public int MaxKeys { get; set; } = 4_096;
    public int FailureThreshold { get; set; } = 5;
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(30);

    internal FailureCircuitBreakerOptions Snapshot()
    {
        if (MaxKeys is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxKeys));
        if (FailureThreshold is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(FailureThreshold));
        if (OpenDuration <= TimeSpan.Zero || OpenDuration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(OpenDuration));
        return new FailureCircuitBreakerOptions { MaxKeys = MaxKeys, FailureThreshold = FailureThreshold, OpenDuration = OpenDuration };
    }
}

public sealed class FailureCircuitBreaker
{
    private readonly FailureCircuitBreakerOptions _options;
    private readonly Dictionary<string, Circuit> _circuits = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public FailureCircuitBreaker(FailureCircuitBreakerOptions? options = null)
    {
        _options = (options ?? new FailureCircuitBreakerOptions()).Snapshot();
    }

    public bool TryEnter(string key, DateTimeOffset now)
    {
        Validate(key);
        lock (_sync)
        {
            if (!_circuits.TryGetValue(key, out var circuit)) return true;
            if (circuit.OpenUntil is null) return true;
            if (now < circuit.OpenUntil.Value) return false;
            if (circuit.HalfOpenInFlight && now < circuit.HalfOpenUntil) return false;
            circuit.HalfOpenInFlight = true;
            circuit.HalfOpenUntil = SafeAdd(now, _options.OpenDuration);
            return true;
        }
    }

    public void RecordSuccess(string key)
    {
        Validate(key);
        lock (_sync) _circuits.Remove(key);
    }

    public void RecordFailure(string key, DateTimeOffset now)
    {
        Validate(key);
        lock (_sync)
        {
            if (!_circuits.TryGetValue(key, out var circuit))
            {
                if (_circuits.Count >= _options.MaxKeys) throw new TenantCapacityExceededException("max_circuit_keys", "The circuit-breaker key set is full.");
                circuit = new Circuit();
                _circuits.Add(key, circuit);
            }
            circuit.HalfOpenInFlight = false;
            if (circuit.Failures < _options.FailureThreshold) circuit.Failures++;
            if (circuit.Failures >= _options.FailureThreshold) circuit.OpenUntil = SafeAdd(now, _options.OpenDuration);
        }
    }

    private static void Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256) throw new ArgumentException("A bounded circuit key is required.", nameof(key));
    }

    private static DateTimeOffset SafeAdd(DateTimeOffset value, TimeSpan duration) =>
        value > DateTimeOffset.MaxValue - duration ? DateTimeOffset.MaxValue : value + duration;

    private sealed class Circuit
    {
        public int Failures { get; set; }
        public DateTimeOffset? OpenUntil { get; set; }
        public bool HalfOpenInFlight { get; set; }
        public DateTimeOffset HalfOpenUntil { get; set; }
    }
}
