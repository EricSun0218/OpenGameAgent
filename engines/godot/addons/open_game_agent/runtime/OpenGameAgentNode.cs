using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using OpenGameAgent.EngineClient;

namespace OpenGameAgent.Godot;

/// <summary>
/// Main-thread bridge from Godot 4 .NET to a separately hosted OpenGameAgent runtime.
/// Agent state and model credentials remain outside the game process.
/// </summary>
public partial class OpenGameAgentNode : Node
{
    [Signal]
    public delegate void StreamEventEventHandler(string eventId, string eventName, string eventJson);

    [Signal]
    public delegate void RequestFailedEventHandler(string category);

    [Export]
    public string ServerUrl { get; set; } = "http://127.0.0.1:4317/";

    [Export(PropertyHint.Range, "1,4096,1")]
    public int MaximumPendingCallbacks { get; set; } = 256;

    [Export(PropertyHint.Range, "1,1024,1")]
    public int MaximumCallbacksPerFrame { get; set; } = 32;

    private readonly ConcurrentQueue<Action> _callbacks = new();
    private CancellationTokenSource? _lifetime;
    private EngineGameAgentClient? _client;
    private Func<CancellationToken, ValueTask<string?>>? _authenticationProvider;
    private int _pendingCallbacks;

    /// <summary>Configures body authentication before the client is first used.</summary>
    public void ConfigureAuthentication(Func<CancellationToken, ValueTask<string?>> provider)
    {
        if (_client is not null) throw new InvalidOperationException("Configure authentication before first use.");
        _authenticationProvider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>Starts one streamed run. Signals are emitted from the Godot main thread.</summary>
    public async Task RunJsonAsync(string inputJson, string? runId = null, CancellationToken cancellationToken = default)
    {
        EngineGameAgentClient client = EnsureClient();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            EnsureLifetime().Token,
            cancellationToken);
        try
        {
            await client.RunAsync(inputJson, QueueEventAsync, runId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (EngineGameAgentClientException error)
        {
            QueueCallback(() => EmitSignal(SignalName.RequestFailed, error.Category));
        }
        catch
        {
            QueueCallback(() => EmitSignal(SignalName.RequestFailed, "client-failure"));
            throw;
        }
    }

    /// <summary>Streams pending durable action deliveries on the same main-thread event channel.</summary>
    public async Task StreamActionsJsonAsync(
        string sessionJson,
        int maximum = 1,
        CancellationToken cancellationToken = default)
    {
        EngineGameAgentClient client = EnsureClient();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            EnsureLifetime().Token,
            cancellationToken);
        try
        {
            await client.StreamActionsAsync(sessionJson, QueueEventAsync, maximum, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (EngineGameAgentClientException error)
        {
            QueueCallback(() => EmitSignal(SignalName.RequestFailed, error.Category));
        }
    }

    public Task<bool> SteerJsonAsync(
        string sessionJson,
        string expectedRunCoordinateJson,
        string inputJson,
        CancellationToken cancellationToken = default)
        => EnsureClient().SteerAsync(sessionJson, expectedRunCoordinateJson, inputJson, cancellationToken);

    public Task<bool> FollowUpJsonAsync(
        string sessionJson,
        string expectedRunCoordinateJson,
        string inputJson,
        CancellationToken cancellationToken = default)
        => EnsureClient().FollowUpAsync(sessionJson, expectedRunCoordinateJson, inputJson, cancellationToken);

    public Task<bool> AbortJsonAsync(
        string sessionJson,
        string expectedRunCoordinateJson,
        CancellationToken cancellationToken = default)
        => EnsureClient().AbortAsync(sessionJson, expectedRunCoordinateJson, cancellationToken);

    public EngineGameAgentClient Client => EnsureClient();

    public override void _Process(double delta)
    {
        int maximum = Math.Max(1, MaximumCallbacksPerFrame);
        for (int index = 0; index < maximum && _callbacks.TryDequeue(out Action? callback); index++)
        {
            Interlocked.Decrement(ref _pendingCallbacks);
            callback();
        }
    }

    public override void _ExitTree()
    {
        CancellationTokenSource? lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is not null)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
        Interlocked.Exchange(ref _client, null)?.Dispose();
        while (_callbacks.TryDequeue(out _)) Interlocked.Decrement(ref _pendingCallbacks);
    }

    private Task QueueEventAsync(EngineGameAgentEvent item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueueCallback(() => EmitSignal(SignalName.StreamEvent, item.Id, item.Name, item.Json));
        return Task.CompletedTask;
    }

    private void QueueCallback(Action callback)
    {
        int pending = Interlocked.Increment(ref _pendingCallbacks);
        if (pending > Math.Clamp(MaximumPendingCallbacks, 1, 4096))
        {
            Interlocked.Decrement(ref _pendingCallbacks);
            throw new InvalidOperationException("The OpenGameAgent main-thread callback queue is full.");
        }
        _callbacks.Enqueue(callback);
    }

    private EngineGameAgentClient EnsureClient()
    {
        if (_client is not null) return _client;
        var options = new EngineGameAgentClientOptions(new Uri(ServerUrl))
        {
            AuthenticationJsonProvider = _authenticationProvider,
        };
        var created = new EngineGameAgentClient(options);
        EngineGameAgentClient? existing = Interlocked.CompareExchange(ref _client, created, null);
        if (existing is null) return created;
        created.Dispose();
        return existing;
    }

    private CancellationTokenSource EnsureLifetime()
    {
        if (_lifetime is not null) return _lifetime;
        var created = new CancellationTokenSource();
        CancellationTokenSource? existing = Interlocked.CompareExchange(ref _lifetime, created, null);
        if (existing is null) return created;
        created.Dispose();
        return existing;
    }
}
