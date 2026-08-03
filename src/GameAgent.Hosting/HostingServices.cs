using GameAgent.Protocol;
using GameAgent.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace GameAgent.Hosting;

public sealed class GameAgentHostingState
{
    private int _started;
    private int _stopping;

    public bool IsStarted => Volatile.Read(ref _started) != 0;
    public bool IsStopping => Volatile.Read(ref _stopping) != 0;

    internal void MarkStarted() => Volatile.Write(ref _started, 1);
    internal void MarkStopping() => Volatile.Write(ref _stopping, 1);
}

public sealed class GameAgentHostingHealthCheck : IHealthCheck
{
    private readonly GameAgentHostingState _state;
    private readonly TenantAdmissionController _admission;

    public GameAgentHostingHealthCheck(GameAgentHostingState state, TenantAdmissionController admission)
    {
        _state = state;
        _admission = admission;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _admission.GetSnapshot();
        var data = new Dictionary<string, object>
        {
            ["tenants"] = snapshot.TenantCount,
            ["activeRuns"] = snapshot.ActiveRuns,
            ["waitingRuns"] = snapshot.WaitingRuns
        };
        if (!_state.IsStarted || _state.IsStopping)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The Agent host is not accepting work.", data: data));
        }
        return Task.FromResult(HealthCheckResult.Healthy("The Agent host is accepting work.", data));
    }
}

public sealed class GameAgentHostingLifecycle : IHostedService
{
    private readonly GameAgentHostingState _state;
    private readonly GameAgentKillSwitch _killSwitch;

    public GameAgentHostingLifecycle(GameAgentHostingState state, GameAgentKillSwitch killSwitch)
    {
        _state = state;
        _killSwitch = killSwitch;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _killSwitch.AllowAll();
        _state.MarkStarted();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _killSwitch.BlockAll();
        _state.MarkStopping();
        return Task.CompletedTask;
    }
}

public static class GameAgentHostingServiceCollectionExtensions
{
    public static IServiceCollection AddGameAgentHosting(
        this IServiceCollection services,
        Action<TenantAdmissionOptions>? configureAdmission = null,
        Action<AgentEventReplayOptions>? configureReplay = null,
        Action<RemoteActionBrokerOptions>? configureRemoteActions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var admissionOptions = new TenantAdmissionOptions();
        configureAdmission?.Invoke(admissionOptions);
        var replayOptions = new AgentEventReplayOptions();
        configureReplay?.Invoke(replayOptions);
        var remoteOptions = new RemoteActionBrokerOptions();
        configureRemoteActions?.Invoke(remoteOptions);

        services.AddLogging();
        services.AddSingleton(new TenantAdmissionController(admissionOptions));
        services.AddSingleton<AgentTransportCodec>();
        services.AddSingleton(provider => new AgentEventReplayBuffer(
            replayOptions,
            provider.GetRequiredService<AgentTransportCodec>()));
        services.AddSingleton<GameAgentKillSwitch>();
        services.AddSingleton<TenantRateLimiter>();
        services.AddSingleton<FailureCircuitBreaker>();
        services.AddSingleton(provider => new RemoteActionBroker(
            provider.GetRequiredService<AgentTransportCodec>(),
            remoteOptions,
            provider.GetRequiredService<GameAgentKillSwitch>()));
        services.AddSingleton<GameAgentHostingState>();
        services.AddSingleton<GameAgentHostingLifecycle>();
        services.AddSingleton<IHostedService>(static provider => provider.GetRequiredService<GameAgentHostingLifecycle>());
        services.AddSingleton<GameAgentHostingHealthCheck>();
        services.AddHealthChecks().AddCheck<GameAgentHostingHealthCheck>(
            "game_agent_host",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" });
        return services;
    }
}

public sealed class TenantRuntimeRegistry : IAsyncDisposable
{
    private readonly int _maximumRuntimes;
    private readonly Dictionary<string, RuntimeEntry> _runtimes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public TenantRuntimeRegistry(int maximumRuntimes = 1_024)
    {
        if (maximumRuntimes is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRuntimes));
        }
        _maximumRuntimes = maximumRuntimes;
    }

    public async ValueTask<BuiltGameAgentRuntime> GetOrAddAsync(
        string tenantWorldKey,
        Func<CancellationToken, ValueTask<BuiltGameAgentRuntime>> factory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantWorldKey) || tenantWorldKey.Length > 512)
        {
            throw new ArgumentException("A bounded tenant-world key is required.", nameof(tenantWorldKey));
        }
        ArgumentNullException.ThrowIfNull(factory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RuntimeEntry entry;
        var createsRuntime = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_runtimes.TryGetValue(tenantWorldKey, out var existing))
            {
                entry = existing;
            }
            else
            {
                if (_runtimes.Count >= _maximumRuntimes)
                {
                    throw new TenantCapacityExceededException("max_runtime_instances", "The runtime registry is full.");
                }
                entry = new RuntimeEntry();
                _runtimes.Add(tenantWorldKey, entry);
                createsRuntime = true;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (createsRuntime)
        {
            _ = CreateAndPublishAsync(tenantWorldKey, entry, factory);
        }
        return await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveAsync(string tenantWorldKey, CancellationToken cancellationToken = default)
    {
        RuntimeEntry? entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_runtimes.Remove(tenantWorldKey, out entry))
            {
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
        var runtime = await entry.Completion.Task.ConfigureAwait(false);
        await runtime.StopAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        RuntimeEntry[] entries;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entries = _runtimes.Values.ToArray();
            _runtimes.Clear();
        }
        finally
        {
            _gate.Release();
        }
        var failures = new List<Exception>();
        foreach (var entry in entries)
        {
            BuiltGameAgentRuntime runtime;
            try
            {
                runtime = await entry.Completion.Task.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                continue;
            }
            try
            {
                await runtime.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                failures.Add(exception);
            }
        }
        _gate.Dispose();
        _shutdown.Dispose();
        if (failures.Count > 0)
        {
            throw new AggregateException("One or more tenant runtimes failed to stop.", failures);
        }
    }

    private async Task CreateAndPublishAsync(
        string tenantWorldKey,
        RuntimeEntry entry,
        Func<CancellationToken, ValueTask<BuiltGameAgentRuntime>> factory)
    {
        try
        {
            var created = await factory(_shutdown.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The runtime factory returned null.");
            entry.Completion.TrySetResult(created);
        }
        catch (OperationCanceledException exception)
        {
            await RemoveFailedEntryAsync(tenantWorldKey, entry).ConfigureAwait(false);
            entry.Completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await RemoveFailedEntryAsync(tenantWorldKey, entry).ConfigureAwait(false);
            entry.Completion.TrySetException(exception);
        }
    }

    private async Task RemoveFailedEntryAsync(string tenantWorldKey, RuntimeEntry failedEntry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runtimes.TryGetValue(tenantWorldKey, out var current)
                && ReferenceEquals(current, failedEntry))
            {
                _runtimes.Remove(tenantWorldKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class RuntimeEntry
    {
        public TaskCompletionSource<BuiltGameAgentRuntime> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
