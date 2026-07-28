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
    private IDurableSessionStore? _store;
    private IOperationLedger? _ledger;
    private bool _ownsStore;
    private IRuntimeClock _clock = new SystemRuntimeClock();
    private IRuntimeIdGenerator _ids = new GuidRuntimeIdGenerator();
    private IRuntimeEventPublisher? _publisher;
    private ISkillAdmissionPolicy? _skillAdmissionPolicy;
    private IToolDisclosurePolicy? _toolDisclosurePolicy;
    private ProviderRetryPolicy _retryPolicy = new();
    private DurableAgentRuntimeOptions _runtimeOptions = new();
    private ContextCompilerOptions _contextOptions = new();
    private ToolSchedulerLimits _schedulerLimits = new();
    private readonly object _disposeSync = new();
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
        _tools = tools?.ToArray()
                 ?? throw new ArgumentNullException(nameof(tools));
        return this;
    }

    public GameAgentRuntimeBuilder WithSkills(
        IEnumerable<SkillManifest> skills)
    {
        ThrowIfFinished();
        _skills = skills?.ToArray()
                  ?? throw new ArgumentNullException(nameof(skills));
        return this;
    }

    public GameAgentRuntimeBuilder WithSkillAdmissionPolicy(
        ISkillAdmissionPolicy policy)
    {
        ThrowIfFinished();
        _skillAdmissionPolicy =
            policy ?? throw new ArgumentNullException(nameof(policy));
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

    public GameAgentRuntimeBuilder WithRetryPolicy(
        ProviderRetryPolicy policy)
    {
        ThrowIfFinished();
        _retryPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
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

    public GameAgentRuntimeBuilder WithContextOptions(
        ContextCompilerOptions options)
    {
        ThrowIfFinished();
        _contextOptions =
            options ?? throw new ArgumentNullException(nameof(options));
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

    public GameAgentRuntimeBuilder PublishEventsTo(
        IRuntimeEventPublisher publisher)
    {
        ThrowIfFinished();
        _publisher =
            publisher ?? throw new ArgumentNullException(nameof(publisher));
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

            var toolRegistry = new ToolCatalogRegistry();
            toolRegistry.Replace(_tools);
            var skillRegistry = new SkillCatalogRegistry();
            skillRegistry.Replace(_skills);
            var journal = new JournalCoordinator(
                _store,
                _ledger,
                _clock,
                _ids,
                _publisher);
            _ownedDisposables.Add(journal);
            var runtime = new DurableAgentRuntime(
                new ProviderAttemptRunner(
                    _providers.ToArray(),
                    _retryPolicy,
                    new SystemRuntimeDelay(),
                    _ids),
                _host,
                journal,
                new RunRecovery(_store, _ledger, journal),
                toolRegistry,
                skillRegistry,
                new ContextCompiler(_contextOptions),
                new ToolBatchPlanner(_schedulerLimits),
                new ToolBatchScheduler(_schedulerLimits),
                _clock,
                _ids,
                _runtimeOptions,
                skillAdmissionPolicy: _skillAdmissionPolicy,
                toolDisclosurePolicy: _toolDisclosurePolicy);
            var result = new BuiltGameAgentRuntime(
                runtime,
                toolRegistry,
                skillRegistry,
                _store,
                _ownsStore,
                _ownedDisposables.ToArray());

            _store = null;
            _ledger = null;
            _ownsStore = false;
            _ownedDisposables.Clear();
            Interlocked.Exchange(ref _finished, 1);
            return result;
        }
        catch
        {
            Interlocked.Exchange(ref _finished, 1);
            DisposeOwnedDisposables();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task dispose;
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            dispose = _disposeTask;
        }

        return new ValueTask(dispose);
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _finished, 1);
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
    private readonly object _shutdownSync = new();
    private Task? _shutdownTask;

    internal BuiltGameAgentRuntime(
        DurableAgentRuntime runtime,
        ToolCatalogRegistry tools,
        SkillCatalogRegistry skills,
        IDurableSessionStore store,
        bool ownsStore,
        IReadOnlyList<IDisposable> ownedDisposables)
    {
        Runtime = runtime;
        Tools = tools;
        Skills = skills;
        _store = store;
        _ownsStore = ownsStore;
        _ownedDisposables = ownedDisposables;
    }

    public DurableAgentRuntime Runtime { get; }

    public ToolCatalogRegistry Tools { get; }

    public SkillCatalogRegistry Skills { get; }

    public IDurableSessionStore SessionStore => _store;

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task shutdown;
        lock (_shutdownSync)
        {
            _shutdownTask ??= StopCoreAsync();
            shutdown = _shutdownTask;
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(WaitForShutdownAsync(shutdown, cancellationToken))
            : new ValueTask(shutdown);
    }

    public ValueTask DisposeAsync() => StopAsync();

    private async Task StopCoreAsync()
    {
        List<Exception>? errors = null;
        try
        {
            await Runtime.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (errors ??= new List<Exception>()).Add(exception);
        }

        try
        {
            await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (errors ??= new List<Exception>()).Add(exception);
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
