using System.Runtime.ExceptionServices;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Providers.OpenAICompatible;

namespace GameAgent.Runtime;

public sealed class GameAgentRuntimeBuilder : IAsyncDisposable
{
    private readonly IGameHost _host;
    private readonly List<IStreamingModelProvider> _providers = new();
    private readonly List<IDisposable> _ownedDisposables = new();
    private IReadOnlyList<ToolDescriptor> _tools = Array.Empty<ToolDescriptor>();
    private IReadOnlyList<SkillManifest> _skills = Array.Empty<SkillManifest>();
    private ToolCatalogRegistry? _toolRegistry;
    private SkillCatalogRegistry? _skillRegistry;
    private bool _toolsConfigured;
    private bool _skillsConfigured;
    private IDurableSessionStore? _store;
    private IOperationLedger? _ledger;
    private bool _ownsStore;
    private IRuntimeClock _clock = new SystemRuntimeClock();
    private IRuntimeIdGenerator _ids = new GuidRuntimeIdGenerator();
    private IRuntimeEventPublisher? _publisher;
    private ISkillAdmissionPolicy? _skillAdmissionPolicy;
    private ISkillContentResolver? _skillContentResolver;
    private IToolDisclosurePolicy? _toolDisclosurePolicy;
    private IFinalOutputAdmissionPolicy? _finalOutputAdmissionPolicy;
    private IConversationCompactor? _conversationCompactor;
    private IConversationContextEngine? _conversationContextEngine;
    private IReadOnlyList<AgentLifecycleMiddlewareRegistration>
        _lifecycleMiddlewares =
            Array.Empty<AgentLifecycleMiddlewareRegistration>();
    private AgentLifecyclePipelineOptions? _lifecycleOptions;
    private RuntimeMemoryLifecycle? _memoryLifecycle;
    private IRuntimeMemoryPolicy? _memoryPolicy;
    private RuntimeMemoryIntegrationOptions? _memoryOptions;
    private bool _ownsMemoryLifecycle;
    private ProviderRetryPolicy _retryPolicy = new();
    private ProviderRouteResilienceOptions _providerRouteResilience = new();
    private DurableAgentRuntimeOptions _runtimeOptions = new();
    private ChildAgentSupervisorOptions _childAgentOptions = new();
    private RunRecoveryOptions _recoveryOptions = new();
    private ContextCompilerOptions _contextOptions = new();
    private IRuntimeTokenEstimator _tokenEstimator =
        ScriptAwareTokenEstimator.Shared;
    private IRuntimeMetricsSink? _metricsSink;
    private RuntimeMetricsOptions? _metricsOptions;
    private IExecutionRoutePolicy? _executionRoutePolicy;
    private ExecutionRouterOptions? _executionRouterOptions;
    private IRoutedWorkflowRuntime? _routedWorkflowRuntime;
    private ToolSchedulerLimits _schedulerLimits = new();
    private readonly object _disposeSync = new();
    private int _disposeRetryRequired;
    private Task? _disposeTask;
    private int _finished;

    public GameAgentRuntimeBuilder(IGameHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public GameAgentRuntimeBuilder UseFileJournal(
        string path,
        FileJournalOptions? options = null)
    {
        ThrowIfFinished();
        if (_store is not null)
        {
            throw new InvalidOperationException(
                "A durable store is already configured.");
        }

        var store = new FileSessionStore(path, options);
        _store = store;
        _ledger = store;
        _ownsStore = true;
        return this;
    }

    public GameAgentRuntimeBuilder UseDurableStore(
        IDurableSessionStore store,
        IOperationLedger ledger,
        bool disposeOnShutdown = false)
    {
        ThrowIfFinished();
        if (_store is not null)
        {
            throw new InvalidOperationException(
                "A durable store is already configured.");
        }

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _ownsStore = disposeOnShutdown;
        return this;
    }

    public GameAgentRuntimeBuilder UseOpenAiCompatibleProvider(
        OpenAiCompatibleProviderOptions options,
        IProviderCredentialSource credentials)
    {
        ThrowIfFinished();
        var transport = new HttpClientStreamingTransport();
        try
        {
            _providers.Add(
                new OpenAiCompatibleStreamingProvider(
                    options,
                    credentials,
                    transport));
            _ownedDisposables.Add(transport);
            return this;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    public GameAgentRuntimeBuilder AddProvider(
        IStreamingModelProvider provider)
    {
        ThrowIfFinished();
        _providers.Add(
            provider ?? throw new ArgumentNullException(nameof(provider)));
        return this;
    }

    public GameAgentRuntimeBuilder WithTools(
        IEnumerable<ToolDescriptor> tools)
    {
        ThrowIfFinished();
        if (_toolRegistry is not null)
        {
            throw new InvalidOperationException(
                "A tool registry is already configured.");
        }

        _tools = CopyCatalogBounded(
            tools,
            RegistryLimits.DefaultMaxTools,
            nameof(tools),
            "tool_count_exceeded");
        _toolsConfigured = true;
        return this;
    }

    public GameAgentRuntimeBuilder WithSkills(
        IEnumerable<SkillManifest> skills)
    {
        ThrowIfFinished();
        if (_skillRegistry is not null)
        {
            throw new InvalidOperationException(
                "A skill registry is already configured.");
        }

        _skills = CopyCatalogBounded(
            skills,
            RegistryLimits.DefaultMaxSkills,
            nameof(skills),
            "skill_count_exceeded");
        _skillsConfigured = true;
        return this;
    }

    /// <summary>
    /// Uses a host-owned tool registry without replacing its current
    /// snapshot. Explicit catalog reloads become visible to the next
    /// RunAsync or ResumeAsync agent-loop invocation; an active invocation
    /// keeps one immutable catalog for all of its turns.
    /// </summary>
    public GameAgentRuntimeBuilder WithToolRegistry(
        ToolCatalogRegistry registry)
    {
        ThrowIfFinished();
        if (_toolsConfigured)
        {
            throw new InvalidOperationException(
                "A static tool catalog is already configured.");
        }

        _toolRegistry =
            registry ?? throw new ArgumentNullException(nameof(registry));
        return this;
    }

    /// <summary>
    /// Uses a host-owned skill registry without replacing its current
    /// snapshot. A local package catalog can bind this same registry and act
    /// as the configured content resolver, so an atomic reload is visible to
    /// subsequent RunAsync or ResumeAsync agent-loop invocations.
    /// </summary>
    public GameAgentRuntimeBuilder WithSkillRegistry(
        SkillCatalogRegistry registry)
    {
        ThrowIfFinished();
        if (_skillsConfigured)
        {
            throw new InvalidOperationException(
                "A static skill catalog is already configured.");
        }

        _skillRegistry =
            registry ?? throw new ArgumentNullException(nameof(registry));
        return this;
    }

    private static IReadOnlyList<T> CopyCatalogBounded<T>(
        IEnumerable<T> source,
        int maximumItems,
        string parameterName,
        string limitCode)
    {
        if (source is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        var result = new List<T>(maximumItems);
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (result.Count >= maximumItems)
            {
                throw new RuntimeContentLimitException(
                    parameterName,
                    limitCode,
                    $"The catalog exceeds {maximumItems} items.");
            }
            result.Add(enumerator.Current);
        }

        return result.ToArray();
    }

    public GameAgentRuntimeBuilder WithSkillAdmissionPolicy(
        ISkillAdmissionPolicy policy)
    {
        ThrowIfFinished();
        _skillAdmissionPolicy =
            policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public GameAgentRuntimeBuilder WithSkillContentResolver(
        ISkillContentResolver resolver)
    {
        ThrowIfFinished();
        _skillContentResolver =
            resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
    }

    public GameAgentRuntimeBuilder WithToolDisclosurePolicy(
        IToolDisclosurePolicy policy)
    {
        ThrowIfFinished();
        _toolDisclosurePolicy =
            policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public GameAgentRuntimeBuilder WithFinalOutputAdmissionPolicy(
        IFinalOutputAdmissionPolicy policy)
    {
        ThrowIfFinished();
        _finalOutputAdmissionPolicy =
            policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public GameAgentRuntimeBuilder WithConversationCompactor(
        IConversationCompactor compactor)
    {
        ThrowIfFinished();
        if (_conversationContextEngine is not null)
        {
            throw new InvalidOperationException(
                "A custom conversation context engine is already configured.");
        }

        _conversationCompactor =
            compactor ?? throw new ArgumentNullException(nameof(compactor));
        return this;
    }

    public GameAgentRuntimeBuilder WithConversationContextEngine(
        IConversationContextEngine engine)
    {
        ThrowIfFinished();
        if (_conversationCompactor is not null)
        {
            throw new InvalidOperationException(
                "A built-in conversation compactor is already configured.");
        }

        _conversationContextEngine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        return this;
    }

    public GameAgentRuntimeBuilder WithRuntimeMemory(
        RuntimeMemoryLifecycle lifecycle,
        IRuntimeMemoryPolicy policy,
        RuntimeMemoryIntegrationOptions? options = null,
        bool disposeOnShutdown = false)
    {
        ThrowIfFinished();
        if (_memoryLifecycle is not null)
        {
            throw new InvalidOperationException(
                "Runtime-managed memory is already configured.");
        }

        _memoryLifecycle = lifecycle
                           ?? throw new ArgumentNullException(
                               nameof(lifecycle));
        _memoryPolicy = policy
                        ?? throw new ArgumentNullException(nameof(policy));
        _memoryOptions = options;
        _ownsMemoryLifecycle = disposeOnShutdown;
        return this;
    }

    public GameAgentRuntimeBuilder WithMemory(
        RuntimeMemoryLifecycle lifecycle,
        IRuntimeMemoryPolicy policy,
        RuntimeMemoryIntegrationOptions? options = null,
        bool disposeOnShutdown = false)
    {
        return WithRuntimeMemory(
            lifecycle,
            policy,
            options,
            disposeOnShutdown);
    }

    public GameAgentRuntimeBuilder WithRetryPolicy(
        ProviderRetryPolicy policy)
    {
        ThrowIfFinished();
        _retryPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public GameAgentRuntimeBuilder WithProviderRouteResilience(
        ProviderRouteResilienceOptions options)
    {
        ThrowIfFinished();
        _providerRouteResilience =
            options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    public GameAgentRuntimeBuilder WithRuntimeOptions(
        DurableAgentRuntimeOptions options)
    {
        ThrowIfFinished();
        _runtimeOptions =
            options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    public GameAgentRuntimeBuilder WithChildAgentSupervisorOptions(
        ChildAgentSupervisorOptions options)
    {
        ThrowIfFinished();
        _childAgentOptions = options
                             ?? throw new ArgumentNullException(
                                 nameof(options));
        return this;
    }

    public GameAgentRuntimeBuilder WithRecoveryOptions(
        RunRecoveryOptions options)
    {
        ThrowIfFinished();
        _recoveryOptions =
            options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    public GameAgentRuntimeBuilder WithContextOptions(
        ContextCompilerOptions options)
    {
        ThrowIfFinished();
        _contextOptions =
            options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    public GameAgentRuntimeBuilder WithTokenEstimator(
        IRuntimeTokenEstimator estimator)
    {
        ThrowIfFinished();
        _tokenEstimator =
            estimator ?? throw new ArgumentNullException(nameof(estimator));
        return this;
    }

    public GameAgentRuntimeBuilder WithSchedulerLimits(
        ToolSchedulerLimits limits)
    {
        ThrowIfFinished();
        _schedulerLimits =
            limits ?? throw new ArgumentNullException(nameof(limits));
        return this;
    }

    public GameAgentRuntimeBuilder WithRuntimeServices(
        IRuntimeClock clock,
        IRuntimeIdGenerator ids)
    {
        ThrowIfFinished();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        return this;
    }

    public GameAgentRuntimeBuilder WithMetrics(
        IRuntimeMetricsSink sink,
        RuntimeMetricsOptions? options = null)
    {
        ThrowIfFinished();
        _metricsSink = sink
                       ?? throw new ArgumentNullException(nameof(sink));
        _metricsOptions = options;
        return this;
    }

    public GameAgentRuntimeBuilder PublishEventsTo(
        IRuntimeEventPublisher publisher)
    {
        ThrowIfFinished();
        _publisher =
            publisher ?? throw new ArgumentNullException(nameof(publisher));
        return this;
    }

    public GameAgentRuntimeBuilder WithLifecycleMiddleware(
        IEnumerable<AgentLifecycleMiddlewareRegistration> middleware,
        AgentLifecyclePipelineOptions? options = null)
    {
        ThrowIfFinished();
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));
        }

        var maximum = options?.MaxMiddlewares ?? 16;
        _lifecycleMiddlewares = CopyCatalogBounded(
            middleware,
            maximum,
            nameof(middleware),
            "lifecycle_middleware_count_exceeded");
        _lifecycleOptions = options;
        return this;
    }

    public GameAgentRuntimeBuilder WithExecutionRoutePolicy(
        IExecutionRoutePolicy policy,
        ExecutionRouterOptions? options = null)
    {
        ThrowIfFinished();
        _executionRoutePolicy = policy
                                ?? throw new ArgumentNullException(
                                    nameof(policy));
        _executionRouterOptions = options;
        return this;
    }

    public GameAgentRuntimeBuilder WithRoutedWorkflowRuntime(
        IRoutedWorkflowRuntime runtime)
    {
        ThrowIfFinished();
        _routedWorkflowRuntime = runtime
                                 ?? throw new ArgumentNullException(
                                     nameof(runtime));
        return this;
    }

    public BuiltGameAgentRuntime Build()
    {
        ThrowIfFinished();
        try
        {
            if (_providers.Count == 0)
            {
                throw new InvalidOperationException(
                    "At least one model provider is required.");
            }

            if (_store is null || _ledger is null)
            {
                throw new InvalidOperationException(
                    "A durable store and operation ledger are required.");
            }

            var toolRegistry = _toolRegistry ?? new ToolCatalogRegistry();
            if (_toolRegistry is null)
            {
                toolRegistry.Replace(_tools);
            }

            var skillRegistry = _skillRegistry
                                ?? new SkillCatalogRegistry();
            if (_skillRegistry is null)
            {
                skillRegistry.Replace(_skills);
            }
            var journal = new JournalCoordinator(
                _store,
                _ledger,
                _clock,
                _ids,
                _publisher);
            _ownedDisposables.Add(journal);
            var providerRunner = new ProviderAttemptRunner(
                _providers.ToArray(),
                _retryPolicy,
                new SystemRuntimeDelay(),
                _ids,
                routeResilienceOptions: _providerRouteResilience,
                clock: _clock);
            var lifecycle = new AgentLifecyclePipeline(
                _lifecycleMiddlewares,
                _lifecycleOptions);
            _ownedDisposables.Add(lifecycle);
            var runtime = new DurableAgentRuntime(
                providerRunner,
                _host,
                journal,
                new RunRecovery(
                    _store,
                    _ledger,
                    journal,
                    _recoveryOptions),
                toolRegistry,
                skillRegistry,
                new ContextCompiler(
                    _contextOptions,
                    _tokenEstimator),
                new ToolBatchPlanner(_schedulerLimits),
                new ToolBatchScheduler(_schedulerLimits),
                _clock,
                _ids,
                _runtimeOptions,
                skillAdmissionPolicy: _skillAdmissionPolicy,
                toolDisclosurePolicy: _toolDisclosurePolicy,
                conversationCompactor: _conversationCompactor,
                memoryLifecycle: _memoryLifecycle,
                memoryPolicy: _memoryPolicy,
                memoryOptions: _memoryOptions,
                skillContentResolver: _skillContentResolver,
                finalOutputAdmissionPolicy:
                    _finalOutputAdmissionPolicy,
                tokenEstimator: _tokenEstimator,
                metricsSink: _metricsSink,
                metricsOptions: _metricsOptions,
                conversationContextEngine: _conversationContextEngine,
                lifecyclePipeline: lifecycle);
            var completion = new SimpleCompletionRuntime(
                providerRunner,
                _ids,
                new SimpleCompletionRuntimeOptions
                {
                    MaxConcurrentProviderCalls =
                        _runtimeOptions.MaxConcurrentProviderCalls,
                    MaxConcurrentBackgroundProviderCalls =
                        _runtimeOptions.MaxConcurrentBackgroundProviderCalls,
                    MaxMessages = Math.Min(
                        _runtimeOptions.MaxTranscriptMessages,
                        4_096),
                    MaxPromptUtf8Bytes = _runtimeOptions.MaxPromptUtf8Bytes,
                    EstimatedPromptBytesPerToken =
                        _runtimeOptions.EstimatedPromptBytesPerToken
                },
                runtime.ProviderAdmission);
            var execution = new RoutedExecutionRuntime(
                runtime,
                _routedWorkflowRuntime,
                _executionRoutePolicy,
                _executionRouterOptions);
            var children = new ChildAgentSupervisor(
                runtime,
                _childAgentOptions);
            var result = new BuiltGameAgentRuntime(
                runtime,
                completion,
                execution,
                children,
                toolRegistry,
                skillRegistry,
                _store,
                _ownsStore,
                _ownedDisposables.ToArray(),
                _memoryLifecycle,
                _ownsMemoryLifecycle);

            _store = null;
            _ledger = null;
            _ownsStore = false;
            _memoryLifecycle = null;
            _memoryPolicy = null;
            _memoryOptions = null;
            _ownsMemoryLifecycle = false;
            _ownedDisposables.Clear();
            Interlocked.Exchange(ref _finished, 1);
            return result;
        }
        catch
        {
            Interlocked.Exchange(ref _finished, 1);
            _ = DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task dispose;
        TaskCompletionSource<bool>? launch = null;
        lock (_disposeSync)
        {
            if (_disposeTask is null
                || (_disposeTask.IsCompleted
                    && Volatile.Read(ref _disposeRetryRequired) != 0))
            {
                Volatile.Write(ref _disposeRetryRequired, 0);
                launch = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = launch.Task;
            }
            dispose = _disposeTask;
        }

        if (launch is not null)
        {
            ObserveDisposeFailure(dispose);
            _ = CompleteSharedDisposeAsync(launch);
        }

        return new ValueTask(dispose);
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _finished, 1);

        if (_ownsMemoryLifecycle && _memoryLifecycle is not null)
        {
            try
            {
                await _memoryLifecycle
                    .WaitForShutdownDrainAsync()
                    .ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref _disposeRetryRequired, 1);
                throw;
            }
        }

        DisposeOwnedDisposables();

        if (_ownsStore && _store is not null)
        {
            try
            {
                await _store.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Builder cleanup cannot safely recover a failed store.
            }
        }

        _store = null;
        _ledger = null;
        _ownsStore = false;
        _memoryLifecycle = null;
        _memoryPolicy = null;
        _memoryOptions = null;
        _ownsMemoryLifecycle = false;
    }

    private async Task CompleteSharedDisposeAsync(
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static void ObserveDisposeFailure(Task dispose)
    {
        _ = dispose.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void DisposeOwnedDisposables()
    {
        foreach (var disposable in _ownedDisposables.AsEnumerable().Reverse())
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Cleanup must continue so a later resource is not leaked.
            }
        }

        _ownedDisposables.Clear();
    }

    private void ThrowIfFinished()
    {
        if (Volatile.Read(ref _finished) != 0)
        {
            throw new ObjectDisposedException(nameof(GameAgentRuntimeBuilder));
        }
    }
}

public sealed class BuiltGameAgentRuntime : IAsyncDisposable
{
    private readonly IDurableSessionStore _store;
    private readonly bool _ownsStore;
    private readonly IReadOnlyList<IDisposable> _ownedDisposables;
    private readonly RuntimeMemoryLifecycle? _memoryLifecycle;
    private readonly bool _ownsMemoryLifecycle;
    private readonly object _shutdownSync = new();
    private int _shutdownRetryRequired;
    private Task? _shutdownTask;

    internal BuiltGameAgentRuntime(
        DurableAgentRuntime runtime,
        SimpleCompletionRuntime completion,
        RoutedExecutionRuntime execution,
        ChildAgentSupervisor children,
        ToolCatalogRegistry tools,
        SkillCatalogRegistry skills,
        IDurableSessionStore store,
        bool ownsStore,
        IReadOnlyList<IDisposable> ownedDisposables,
        RuntimeMemoryLifecycle? memoryLifecycle,
        bool ownsMemoryLifecycle)
    {
        Runtime = runtime;
        Completion = completion;
        Execution = execution;
        Children = children;
        Tools = tools;
        Skills = skills;
        _store = store;
        _ownsStore = ownsStore;
        _ownedDisposables = ownedDisposables;
        _memoryLifecycle = memoryLifecycle;
        _ownsMemoryLifecycle = ownsMemoryLifecycle;
    }

    public DurableAgentRuntime Runtime { get; }

    public SimpleCompletionRuntime Completion { get; }

    public RoutedExecutionRuntime Execution { get; }

    public ChildAgentSupervisor Children { get; }

    public ToolCatalogRegistry Tools { get; }

    public SkillCatalogRegistry Skills { get; }

    public IDurableSessionStore SessionStore => _store;

    public RuntimeMemoryLifecycle? Memory => _memoryLifecycle;

    public bool OwnsMemoryLifecycle => _ownsMemoryLifecycle;

    /// <summary>
    /// Reports whether detached provider calls drained when an owned memory
    /// lifecycle was stopped. Null means memory is external, not configured,
    /// or shutdown has not completed.
    /// </summary>
    public bool? MemoryProviderCallsDrainedOnStop =>
        _ownsMemoryLifecycle
            ? _memoryLifecycle?.DetachedProviderCallsDrainedOnDispose
            : null;

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task shutdown;
        TaskCompletionSource<bool>? launch = null;
        lock (_shutdownSync)
        {
            if (_shutdownTask is null
                || (_shutdownTask.IsCompleted
                    && Volatile.Read(ref _shutdownRetryRequired) != 0))
            {
                Volatile.Write(ref _shutdownRetryRequired, 0);
                launch = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownTask = launch.Task;
            }
            shutdown = _shutdownTask;
        }

        if (launch is not null)
        {
            ObserveShutdownFailure(shutdown);
            _ = CompleteSharedShutdownAsync(launch);
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(WaitForShutdownAsync(shutdown, cancellationToken))
            : new ValueTask(shutdown);
    }

    public ValueTask DisposeAsync() => StopAsync();

    private async Task StopCoreAsync()
    {
        List<Exception>? errors = null;
        var executionStop = StartShutdown(Execution.StopAsync);
        var childrenStop = StartShutdown(Children.StopAsync);
        var completionStop = StartShutdown(
            Completion.StopWithDrainResultAsync);
        var runtimeStop = StartShutdown(Runtime.StopAsync);
        var initialStops = Task.WhenAll(
            executionStop,
            childrenStop,
            completionStop,
            runtimeStop);
        try
        {
            await initialStops.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _shutdownRetryRequired, 1);
            var failures = initialStops.Exception?
                .Flatten()
                .InnerExceptions;
            if (failures is null || failures.Count == 0)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            throw new AggregateException(
                "One or more runtime layers failed to begin shutdown.",
                failures);
        }

        var executionDrained = executionStop.Result;
        var childrenDrained = childrenStop.Result;
        var completionDrained = completionStop.Result;
        try
        {
            await Runtime.WaitForShutdownDrainAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _shutdownRetryRequired, 1);
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        if (!childrenDrained)
        {
            childrenDrained = await Children.StopAsync()
                .ConfigureAwait(false);
        }

        if (!completionDrained)
        {
            completionDrained = await Completion
                .StopWithDrainResultAsync()
                .ConfigureAwait(false);
        }

        if (!completionDrained)
        {
            Volatile.Write(ref _shutdownRetryRequired, 1);
            throw new InvalidOperationException(
                "Stateless completion operations did not drain during shutdown.");
        }

        if (!childrenDrained)
        {
            Volatile.Write(ref _shutdownRetryRequired, 1);
            throw new InvalidOperationException(
                "Child-agent operations did not drain during shutdown.");
        }

        if (!executionDrained)
        {
            executionDrained = await Execution.StopAsync()
                .ConfigureAwait(false);
        }

        if (!executionDrained)
        {
            Volatile.Write(ref _shutdownRetryRequired, 1);
            throw new InvalidOperationException(
                "Routed execution operations did not drain during shutdown.");
        }

        await Execution.DisposeAsync().ConfigureAwait(false);
        await Completion.DisposeAsync().ConfigureAwait(false);
        await Children.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (errors ??= new List<Exception>()).Add(exception);
        }

        if (_ownsMemoryLifecycle && _memoryLifecycle is not null)
        {
            try
            {
                await _memoryLifecycle
                    .WaitForShutdownDrainAsync()
                    .ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref _shutdownRetryRequired, 1);
                throw;
            }
        }

        foreach (var disposable in _ownedDisposables.Reverse())
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                (errors ??= new List<Exception>()).Add(exception);
            }
        }

        if (_ownsStore)
        {
            try
            {
                await _store.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (errors ??= new List<Exception>()).Add(exception);
            }
        }

        if (errors is null)
        {
            return;
        }

        if (errors.Count == 1)
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }

        throw new AggregateException(
            "One or more runtime resources failed to shut down.",
            errors);
    }

    private static Task<T> StartShutdown<T>(Func<ValueTask<T>> start)
    {
        try
        {
            return start().AsTask();
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    private static Task StartShutdown(Func<ValueTask> start)
    {
        try
        {
            return start().AsTask();
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private async Task CompleteSharedShutdownAsync(
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static void ObserveShutdownFailure(Task shutdown)
    {
        _ = shutdown.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static async Task WaitForShutdownAsync(
        Task shutdown,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shutdown.IsCompleted)
        {
            await shutdown.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => cancelled.TrySetCanceled(cancellationToken));
        var completed = await Task.WhenAny(shutdown, cancelled.Task)
            .ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }
}
