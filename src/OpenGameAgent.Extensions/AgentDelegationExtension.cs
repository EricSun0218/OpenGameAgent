using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameAgentDelegationStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed class GameAgentDelegationRecord
{
    public GameAgentDelegationRecord(
        string id,
        string sessionId,
        string actorId,
        long revision,
        GameAgentDelegationStatus status,
        string taskJson,
        int depth,
        GameMoment createdAt,
        string? resultJson = null,
        string? error = null,
        GameAgentDelegateRequest? request = null,
        string? parentDelegationId = null,
        string? rootDelegationId = null,
        string? leaseId = null,
        DateTimeOffset? leaseExpiresAt = null,
        int attempt = 0)
    {
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("Delegation IDs and owners are required.");
        }

        if (revision < 0 || depth < 1 || attempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (!Enum.IsDefined(typeof(GameAgentDelegationStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Id = id;
        SessionId = sessionId;
        ActorId = actorId;
        Revision = revision;
        Status = status;
        TaskJson = RequireJson(taskJson, nameof(taskJson));
        Depth = depth;
        if (string.IsNullOrWhiteSpace(createdAt.TimelineId))
        {
            throw new ArgumentException("A valid creation moment is required.", nameof(createdAt));
        }

        CreatedAt = createdAt;
        ResultJson = resultJson is null ? null : RequireJson(resultJson, nameof(resultJson));
        Error = error;
        Request = request;
        ParentDelegationId = OptionalId(parentDelegationId, nameof(parentDelegationId));
        RootDelegationId = OptionalId(rootDelegationId, nameof(rootDelegationId)) ?? id;
        if (ParentDelegationId is null && !string.Equals(RootDelegationId, id, StringComparison.Ordinal))
        {
            throw new ArgumentException("A root delegation must identify itself as the lineage root.", nameof(rootDelegationId));
        }

        LeaseId = OptionalId(leaseId, nameof(leaseId));
        LeaseExpiresAt = leaseExpiresAt;
        Attempt = attempt;
        if ((LeaseId is null) != (LeaseExpiresAt is null))
        {
            throw new ArgumentException("A delegation lease ID and expiry must be supplied together.", nameof(leaseId));
        }

        if (request is not null
            && (!string.Equals(request.Id, id, StringComparison.Ordinal)
                || !string.Equals(request.TaskJson, TaskJson, StringComparison.Ordinal)
                || request.Depth != depth
                || !string.Equals(request.ParentDelegationId, ParentDelegationId, StringComparison.Ordinal)
                || !string.Equals(request.RootDelegationId, RootDelegationId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The durable delegation request does not match its record identity.", nameof(request));
        }
    }

    public string Id { get; }

    public string SessionId { get; }

    public string ActorId { get; }

    public long Revision { get; }

    public GameAgentDelegationStatus Status { get; }

    public string TaskJson { get; }

    public int Depth { get; }

    public GameMoment CreatedAt { get; }

    public string? ResultJson { get; }

    public string? Error { get; }

    /// <summary>
    /// Opaque host recovery data. It is persisted by official stores but deliberately excluded from
    /// model/tool JSON projections so inherited transcript and authority cannot leak through status reads.
    /// </summary>
    [JsonIgnore]
    public GameAgentDelegateRequest? Request { get; }

    public string? ParentDelegationId { get; }

    public string RootDelegationId { get; }

    [JsonIgnore]
    public string? LeaseId { get; }

    [JsonIgnore]
    public DateTimeOffset? LeaseExpiresAt { get; }

    public int Attempt { get; }

    public bool IsRecoverable(DateTimeOffset now) =>
        Request is not null
        && (Status == GameAgentDelegationStatus.Pending
            || (Status == GameAgentDelegationStatus.Running && LeaseExpiresAt <= now));

    private static string RequireJson(string value, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
            return value;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The value must contain valid JSON.", name, exception);
        }
    }

    private static string? OptionalId(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A delegation lineage or lease ID must be bounded and printable.", name);
        }

        return value;
    }
}

public sealed class GameAgentDelegationSaveResult
{
    public GameAgentDelegationSaveResult(bool saved, GameAgentDelegationRecord current)
    {
        Saved = saved;
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public bool Saved { get; }

    public GameAgentDelegationRecord Current { get; }
}

public interface IGameAgentDelegationStore
{
    ValueTask<GameAgentDelegationRecord?> LoadAsync(
        string sessionId,
        string actorId,
        string id,
        CancellationToken cancellationToken);

    ValueTask<GameAgentDelegationSaveResult> SaveAsync(
        GameAgentDelegationRecord record,
        long expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameAgentDelegationRecord>> ListRecoverableAsync(
        DateTimeOffset now,
        int maximum,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameAgentDelegationRecord>> ListAsync(
        string sessionId,
        string actorId,
        string? rootDelegationId,
        int maximum,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameAgentDelegationStore : IGameAgentDelegationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string SessionId, string ActorId, string Id), GameAgentDelegationRecord> _records = new();
    private readonly int _capacity;

    public InMemoryGameAgentDelegationStore(int capacity = 10_000)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask<GameAgentDelegationRecord?> LoadAsync(
        string sessionId,
        string actorId,
        string id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = RequireKey(sessionId, actorId, id);

        lock (_gate)
        {
            return new ValueTask<GameAgentDelegationRecord?>(_records.TryGetValue(key, out var value) ? value : null);
        }
    }

    public ValueTask<GameAgentDelegationSaveResult> SaveAsync(
        GameAgentDelegationRecord record,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var key = (record.SessionId, record.ActorId, record.Id);
        lock (_gate)
        {
            if (_records.TryGetValue(key, out var current))
            {
                if (!string.Equals(current.SessionId, record.SessionId, StringComparison.Ordinal)
                    || !string.Equals(current.ActorId, record.ActorId, StringComparison.Ordinal)
                    || !string.Equals(current.TaskJson, record.TaskJson, StringComparison.Ordinal)
                    || current.Depth != record.Depth
                    || current.CreatedAt != record.CreatedAt
                    || !SameRecoveryIdentity(current, record))
                {
                    throw new InvalidOperationException("A delegation record cannot change ownership or task identity.");
                }

                if (current.Revision != expectedRevision)
                {
                    return new ValueTask<GameAgentDelegationSaveResult>(new GameAgentDelegationSaveResult(false, current));
                }

                if (IsTerminal(current.Status))
                {
                    throw new InvalidOperationException("A terminal delegation record is immutable.");
                }
            }
            else if (expectedRevision != 0)
            {
                return new ValueTask<GameAgentDelegationSaveResult>(new GameAgentDelegationSaveResult(
                    false,
                    new GameAgentDelegationRecord(
                        record.Id,
                        record.SessionId,
                        record.ActorId,
                        0,
                        GameAgentDelegationStatus.Pending,
                        record.TaskJson,
                        record.Depth,
                        record.CreatedAt)));
            }
            else if (_records.Count >= _capacity)
            {
                throw new InvalidOperationException("The delegation store reached its capacity.");
            }

            if (record.Revision != checked(expectedRevision + 1))
            {
                throw new ArgumentException("A delegation revision must advance by exactly one.", nameof(record));
            }

            _records[key] = record;
            return new ValueTask<GameAgentDelegationSaveResult>(new GameAgentDelegationSaveResult(true, record));
        }
    }

    public ValueTask<IReadOnlyList<GameAgentDelegationRecord>> ListRecoverableAsync(
        DateTimeOffset now,
        int maximum,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMaximum(maximum);
        lock (_gate)
        {
            IReadOnlyList<GameAgentDelegationRecord> records = _records.Values
                .Where(record => record.IsRecoverable(now))
                .OrderBy(record => record.CreatedAt.TimelineId, StringComparer.Ordinal)
                .ThenBy(record => record.CreatedAt.Tick)
                .ThenBy(record => record.Id, StringComparer.Ordinal)
                .Take(maximum)
                .ToArray();
            return new ValueTask<IReadOnlyList<GameAgentDelegationRecord>>(records);
        }
    }

    public ValueTask<IReadOnlyList<GameAgentDelegationRecord>> ListAsync(
        string sessionId,
        string actorId,
        string? rootDelegationId,
        int maximum,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireKey(sessionId, actorId, "list");
        ValidateOptionalId(rootDelegationId, nameof(rootDelegationId));
        ValidateMaximum(maximum);
        lock (_gate)
        {
            IReadOnlyList<GameAgentDelegationRecord> records = _records.Values
                .Where(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
                                 && string.Equals(record.ActorId, actorId, StringComparison.Ordinal)
                                 && (rootDelegationId is null
                                     || string.Equals(record.RootDelegationId, rootDelegationId, StringComparison.Ordinal)))
                .OrderBy(record => record.CreatedAt.Tick)
                .ThenBy(record => record.Id, StringComparer.Ordinal)
                .Take(maximum)
                .ToArray();
            return new ValueTask<IReadOnlyList<GameAgentDelegationRecord>>(records);
        }
    }

    private static (string SessionId, string ActorId, string Id) RequireKey(
        string sessionId,
        string actorId,
        string id)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(actorId)
            || string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Delegation IDs and owners are required.");
        }

        return (sessionId, actorId, id);
    }

    private static bool IsTerminal(GameAgentDelegationStatus status) =>
        status is GameAgentDelegationStatus.Completed
            or GameAgentDelegationStatus.Failed
            or GameAgentDelegationStatus.Cancelled;

    private static bool SameRecoveryIdentity(GameAgentDelegationRecord left, GameAgentDelegationRecord right) =>
        string.Equals(left.ParentDelegationId, right.ParentDelegationId, StringComparison.Ordinal)
        && string.Equals(left.RootDelegationId, right.RootDelegationId, StringComparison.Ordinal)
        && SameRequest(left.Request, right.Request);

    private static bool SameRequest(GameAgentDelegateRequest? left, GameAgentDelegateRequest? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.MaximumTurns == right.MaximumTurns
               && left.InheritContext == right.InheritContext
               && GameAgentValueComparer.MessagesEqual(left.ParentMessages, right.ParentMessages)
               && string.Equals(GameAgentWire.SerializeInput(left.ParentInput), GameAgentWire.SerializeInput(right.ParentInput), StringComparison.Ordinal)
               && left.ExecutionScope.IsUnrestricted == right.ExecutionScope.IsUnrestricted
               && left.ExecutionScope.GrantedCapabilities.SequenceEqual(right.ExecutionScope.GrantedCapabilities, StringComparer.Ordinal);
    }

    private static void ValidateMaximum(int maximum)
    {
        if (maximum < 1 || maximum > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }
    }

    private static void ValidateOptionalId(string? value, string name)
    {
        if (value is not null
            && (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)))
        {
            throw new ArgumentException("A delegation lineage ID must be bounded and printable.", name);
        }
    }
}

public sealed class GameAgentDelegateRequest
{
    public GameAgentDelegateRequest(
        string id,
        GameInput parentInput,
        string taskJson,
        int depth,
        int maximumTurns,
        bool inheritContext,
        IReadOnlyList<AgentMessage> parentMessages,
        GameExecutionScope? executionScope = null,
        string? parentDelegationId = null,
        string? rootDelegationId = null,
        GameAgentDelegationLeaseValidator? leaseValidator = null)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("A delegation ID is required.", nameof(id))
            : id;
        ParentInput = parentInput ?? throw new ArgumentNullException(nameof(parentInput));
        TaskJson = RequireJson(taskJson);
        if (depth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        if (maximumTurns < 1 || maximumTurns > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTurns));
        }

        Depth = depth;
        MaximumTurns = maximumTurns;
        InheritContext = inheritContext;
        var messages = (parentMessages ?? throw new ArgumentNullException(nameof(parentMessages))).ToArray();
        if (messages.Any(message => message is null))
        {
            throw new ArgumentException("Delegated parent context cannot contain null messages.", nameof(parentMessages));
        }

        ParentMessages = new ReadOnlyCollection<AgentMessage>(messages);
        ExecutionScope = executionScope ?? GameExecutionScope.Unrestricted;
        ParentDelegationId = OptionalId(parentDelegationId, nameof(parentDelegationId));
        RootDelegationId = OptionalId(rootDelegationId, nameof(rootDelegationId)) ?? Id;
        LeaseValidator = leaseValidator;
        if (ParentDelegationId is null && !string.Equals(RootDelegationId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("A root delegation must identify itself as the lineage root.", nameof(rootDelegationId));
        }
    }

    public string Id { get; }

    public GameInput ParentInput { get; }

    public string TaskJson { get; }

    public int Depth { get; }

    public int MaximumTurns { get; }

    public bool InheritContext { get; }

    public IReadOnlyList<AgentMessage> ParentMessages { get; }

    public GameExecutionScope ExecutionScope { get; }

    public string? ParentDelegationId { get; }

    public string RootDelegationId { get; }

    /// <summary>
    /// Process-local fencing check installed by the delegation extension for one claimed attempt.
    /// Executors must apply it immediately before every tool call. It is never persisted.
    /// </summary>
    [JsonIgnore]
    public GameAgentDelegationLeaseValidator? LeaseValidator { get; }

    internal GameAgentDelegateRequest WithLeaseValidator(GameAgentDelegationLeaseValidator validator) =>
        new(
            Id,
            ParentInput,
            TaskJson,
            Depth,
            MaximumTurns,
            InheritContext,
            ParentMessages,
            ExecutionScope,
            ParentDelegationId,
            RootDelegationId,
            validator ?? throw new ArgumentNullException(nameof(validator)));

    private static string RequireJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
            return value;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The delegated task must contain valid JSON.", nameof(value), exception);
        }
    }

    private static string? OptionalId(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A delegation lineage ID must be bounded and printable.", name);
        }

        return value;
    }
}

public delegate ValueTask<bool> GameAgentDelegationLeaseValidator(CancellationToken cancellationToken);

public sealed class GameAgentDelegateOutcome
{
    public GameAgentDelegateOutcome(
        bool succeeded,
        IReadOnlyList<AgentMessage> messages,
        string? error = null,
        bool cancelled = false)
    {
        if (succeeded && cancelled)
        {
            throw new ArgumentException("A delegated operation cannot be both successful and cancelled.");
        }

        if (succeeded && error is not null)
        {
            throw new ArgumentException("A successful delegated operation cannot contain an error.", nameof(error));
        }

        if (!succeeded && !cancelled && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A failed delegated operation must contain an error.", nameof(error));
        }

        var copy = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
        if (copy.Any(message => message is null))
        {
            throw new ArgumentException("A delegated outcome cannot contain null messages.", nameof(messages));
        }

        Succeeded = succeeded;
        Messages = Array.AsReadOnly(copy);
        Error = error;
        Cancelled = cancelled;
    }

    public bool Succeeded { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public string? Error { get; }

    public bool Cancelled { get; }
}

public interface IGameAgentDelegateHandle : IDisposable
{
    Task<GameAgentDelegateOutcome> Completion { get; }

    bool TrySteer(AgentMessage message);

    bool TryCancel();
}

public interface IGameAgentDelegateExecutor
{
    IGameAgentDelegateHandle Start(GameAgentDelegateRequest request, CancellationToken cancellationToken);
}

public delegate ValueTask<IReadOnlyList<AgentTool>> GameDelegateToolProvider(
    GameAgentDelegateRequest request,
    CancellationToken cancellationToken);

public sealed class LocalGameAgentDelegateExecutor : IGameAgentDelegateExecutor
{
    private readonly IModelProvider _provider;
    private readonly string _model;
    private readonly string _instructions;
    private readonly GameDelegateToolProvider? _tools;
    private readonly AgentLimits _limits;

    public LocalGameAgentDelegateExecutor(
        IModelProvider provider,
        string model,
        string instructions = "Complete the delegated game-agent task and return a concise bounded result.",
        GameDelegateToolProvider? tools = null,
        AgentLimits? limits = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("A model is required.", nameof(model)) : model;
        _instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        _tools = tools;
        var configuredLimits = limits ?? new AgentLimits { MaxTurns = 16, MaxMessages = 256, MaxTotalTokens = 256_000 };
        _limits = LocalHandle.CopyLimits(configuredLimits, configuredLimits.MaxTurns);
    }

    public IGameAgentDelegateHandle Start(GameAgentDelegateRequest request, CancellationToken cancellationToken) =>
        new LocalHandle(
            _provider,
            _model,
            _instructions,
            _tools,
            _limits,
            request ?? throw new ArgumentNullException(nameof(request)),
            cancellationToken);

    private sealed class LocalHandle : IGameAgentDelegateHandle
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Agent _agent;
        private int _disposed;

        public LocalHandle(
            IModelProvider provider,
            string model,
            string instructions,
            GameDelegateToolProvider? tools,
            AgentLimits limits,
            GameAgentDelegateRequest request,
            CancellationToken cancellationToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var options = new AgentOptions(provider, model)
            {
                SystemPrompt = instructions,
                SessionId = request.Id,
                Limits = CopyLimits(limits, request.MaximumTurns),
            };
            if (request.LeaseValidator is not null)
            {
                options.Hooks.AuthorizeToolCallAsync = async (_, token) =>
                    await request.LeaseValidator(token).ConfigureAwait(false)
                        ? ToolCallDecision.Allow()
                        : ToolCallDecision.Block("The delegated execution lease is no longer authoritative.", terminate: true);
            }
            if (request.InheritContext)
            {
                var available = Math.Max(0, options.Limits.MaxMessages - 1);
                var start = Math.Max(0, request.ParentMessages.Count - available);
                while (start < request.ParentMessages.Count
                       && request.ParentMessages[start].Role == AgentRole.Tool)
                {
                    start++;
                }

                for (var index = start; index < request.ParentMessages.Count; index++)
                {
                    options.InitialMessages.Add(request.ParentMessages[index]);
                }
            }

            _agent = new Agent(options);
            Completion = RunAsync(tools, request);
        }

        public Task<GameAgentDelegateOutcome> Completion { get; }

        public bool TrySteer(AgentMessage message) => _agent.TrySteer(message);

        public bool TryCancel()
        {
            if (_cancellation.IsCancellationRequested)
            {
                return false;
            }

            _cancellation.Cancel();
            _agent.TryAbort();
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            TryCancel();
            _cancellation.Dispose();
        }

        private async Task<GameAgentDelegateOutcome> RunAsync(
            GameDelegateToolProvider? tools,
            GameAgentDelegateRequest request)
        {
            try
            {
                if (tools is not null)
                {
                    var contributed = await tools(request, _cancellation.Token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The delegate tool provider returned null.");
                    _agent.SetTools(contributed);
                }

                var result = await _agent.RunAsync(AgentMessage.UserJson(request.TaskJson), _cancellation.Token).ConfigureAwait(false);
                return new GameAgentDelegateOutcome(result.Succeeded, result.NewMessages, result.Error);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return new GameAgentDelegateOutcome(
                    false,
                    Array.Empty<AgentMessage>(),
                    "The delegated agent was cancelled.",
                    cancelled: true);
            }
            catch (Exception exception)
            {
                return new GameAgentDelegateOutcome(false, Array.Empty<AgentMessage>(), exception.Message);
            }
        }

        internal static AgentLimits CopyLimits(AgentLimits source, int maximumTurns) => new()
        {
            MaxSystemPromptCharacters = source.MaxSystemPromptCharacters,
            MaxModelNameCharacters = source.MaxModelNameCharacters,
            MaxSessionIdCharacters = source.MaxSessionIdCharacters,
            MaxTurns = Math.Min(source.MaxTurns, maximumTurns),
            MaxTotalTokens = source.MaxTotalTokens,
            MaxMessages = source.MaxMessages,
            MaxContentPartsPerMessage = source.MaxContentPartsPerMessage,
            MaxTextCharactersPerPart = source.MaxTextCharactersPerPart,
            MaxJsonCharactersPerPart = source.MaxJsonCharactersPerPart,
            MaxResourceUriCharacters = source.MaxResourceUriCharacters,
            MaxToolCallsPerTurn = source.MaxToolCallsPerTurn,
            MaxTools = source.MaxTools,
            MaxToolNameCharacters = source.MaxToolNameCharacters,
            MaxToolCallIdCharacters = source.MaxToolCallIdCharacters,
            MaxToolDescriptionCharacters = source.MaxToolDescriptionCharacters,
            MaxToolSchemaCharacters = source.MaxToolSchemaCharacters,
            MaxMetadataEntriesPerMessage = source.MaxMetadataEntriesPerMessage,
            MaxMetadataKeyCharacters = source.MaxMetadataKeyCharacters,
            MaxMetadataValueCharacters = source.MaxMetadataValueCharacters,
            MaxQueuedMessages = source.MaxQueuedMessages,
            MaxConcurrentTools = source.MaxConcurrentTools,
            ToolTimeoutMilliseconds = source.ToolTimeoutMilliseconds,
            ModelTimeoutMilliseconds = source.ModelTimeoutMilliseconds,
            MaxProgressEventsPerTool = source.MaxProgressEventsPerTool,
            MaxSubscribers = source.MaxSubscribers,
        };
    }
}

public sealed class AgentDelegationExtension : IGameAgentExtension, IAsyncDisposable
{
    private const string DelegateSchema = """
        {"type":"object","required":["task"],"properties":{"delegationId":{"type":"string","minLength":1,"maxLength":256},"parentDelegationId":{"type":"string","minLength":1,"maxLength":256},"task":{},"background":{"type":"boolean"},"inheritContext":{"type":"boolean"},"maxTurns":{"type":"integer","minimum":1,"maximum":128}},"additionalProperties":false}
        """;
    private const string IdSchema = """
        {"type":"object","required":["delegationId"],"properties":{"delegationId":{"type":"string","minLength":1,"maxLength":256}},"additionalProperties":false}
        """;
    private const string SteerSchema = """
        {"type":"object","required":["delegationId","message"],"properties":{"delegationId":{"type":"string","minLength":1,"maxLength":256},"message":{}},"additionalProperties":false}
        """;
    private const string ListSchema = """
        {"type":"object","properties":{"rootDelegationId":{"type":"string","minLength":1,"maxLength":256},"maximum":{"type":"integer","minimum":1,"maximum":256}},"additionalProperties":false}
        """;

    private readonly IGameAgentDelegateExecutor _executor;
    private readonly IGameAgentDelegationStore _store;
    private readonly int _maximumDepth;
    private readonly int _maximumResultCharacters;
    private readonly TimeSpan _settlementTimeout;
    private readonly TimeSpan _leaseDuration;
    private readonly SemaphoreSlim _concurrency;
    private readonly string _workerId = Guid.NewGuid().ToString("N");
    private readonly object _scheduleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<(string SessionId, string ActorId, string Id), IGameAgentDelegateHandle> _active = new();
    private readonly ConcurrentDictionary<(string SessionId, string ActorId, string Id), Task> _running = new();
    private GameAgentExtensionApi? _api;
    private int _disposed;
    private int _resourcesDisposed;

    public AgentDelegationExtension(
        IGameAgentDelegateExecutor executor,
        IGameAgentDelegationStore? store = null,
        int maximumConcurrent = 4,
        int maximumDepth = 3,
        int maximumResultCharacters = 262_144,
        int settlementTimeoutMilliseconds = 10_000,
        int leaseDurationMilliseconds = 60_000)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _store = store ?? new InMemoryGameAgentDelegationStore();
        if (maximumConcurrent < 1 || maximumConcurrent > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrent));
        }

        if (maximumDepth < 1 || maximumDepth > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }

        if (maximumResultCharacters < 1_024 || maximumResultCharacters > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResultCharacters));
        }

        if (settlementTimeoutMilliseconds < 100 || settlementTimeoutMilliseconds > 300_000)
        {
            throw new ArgumentOutOfRangeException(nameof(settlementTimeoutMilliseconds));
        }

        if (leaseDurationMilliseconds < 1_000 || leaseDurationMilliseconds > 3_600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDurationMilliseconds));
        }

        _concurrency = new SemaphoreSlim(maximumConcurrent, maximumConcurrent);
        _maximumDepth = maximumDepth;
        _maximumResultCharacters = maximumResultCharacters;
        _settlementTimeout = TimeSpan.FromMilliseconds(settlementTimeoutMilliseconds);
        _leaseDuration = TimeSpan.FromMilliseconds(leaseDurationMilliseconds);
    }

    public static GameAgentExtensionChannel<GameAgentDelegationRecord> DelegationChanged { get; } =
        new("delegation.changed");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        "opengameagent.delegation",
        "1.1.0",
        "Bounded foreground and background delegated agents with isolated context, lineage, leases, and restart recovery.",
        new[] { "delegation", "multi-agent", "background-work", "steering", "restart-recovery" });

    public void Configure(GameAgentExtensionApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        api.RegisterPromptFragment(
            "delegation-guidance",
            "Delegate only independent or context-heavy subtasks. Give each delegated agent a complete task payload, keep context inheritance opt-in, and retrieve background results by ID.");
        api.RegisterToolProvider(
            "delegation-tools",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                CreateDelegateTool(api, context),
                CreateGetTool(context),
                CreateListTool(context),
                CreateSteerTool(context),
                CreateCancelTool(api, context),
            }));
    }

    /// <summary>
    /// Reclaims pending work and expired running leases after the host has rebuilt its runtime.
    /// Repeated calls are safe because claims use revision CAS and an in-process active registry.
    /// </summary>
    public async ValueTask<int> ResumePendingAsync(
        int maximum = 128,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (maximum < 1 || maximum > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        var api = _api ?? throw new InvalidOperationException("The delegation extension has not been configured by a runtime.");
        var recoverable = await _store.ListRecoverableAsync(DateTimeOffset.UtcNow, maximum, cancellationToken).ConfigureAwait(false);
        var scheduled = 0;
        foreach (var record in recoverable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Request is null)
            {
                continue;
            }

            var key = (record.SessionId, record.ActorId, record.Id);
            Task task;
            lock (_scheduleGate)
            {
                ThrowIfDisposed();
                if (!_running.TryAdd(key, Task.CompletedTask))
                {
                    continue;
                }

                task = RunAsync(api, record, record.Request, _lifetime.Token);
                _running[key] = task;
            }

            scheduled++;
            _ = ObserveAsync(key, task);
        }

        return scheduled;
    }

    public ValueTask<IReadOnlyList<GameAgentDelegationRecord>> ListAsync(
        GameSessionKey key,
        string? rootDelegationId = null,
        int maximum = 128,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _store.ListAsync(key.SessionId, key.ActorId, rootDelegationId, maximum, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        Task[] running;
        lock (_scheduleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            running = _running.Values.ToArray();
        }

        foreach (var handle in _active.Values)
        {
            try
            {
                handle.TryCancel();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        var deferredResourceDisposal = false;
        if (running.Length > 0)
        {
            var completion = Task.WhenAll(running);
            using var timeoutCancellation = new CancellationTokenSource();
            var timeout = Task.Delay(_settlementTimeout, timeoutCancellation.Token);
            var winner = await Task.WhenAny(completion, timeout).ConfigureAwait(false);
            TryCancel(timeoutCancellation);
            if (!ReferenceEquals(winner, completion))
            {
                deferredResourceDisposal = true;
                _ = DisposeResourcesWhenDrainedAsync(completion);
                failures.Add(new TimeoutException(
                    "Delegated agent shutdown exceeded its configured settlement timeout."));
            }

            try
            {
                if (!deferredResourceDisposal)
                {
                    await completion.ConfigureAwait(false);
                }
            }
            catch
            {
                failures.AddRange(running
                    .Where(task => task.IsFaulted && task.Exception is not null)
                    .SelectMany(task => task.Exception!.Flatten().InnerExceptions));
            }
        }

        foreach (var handle in deferredResourceDisposal
                     ? Array.Empty<IGameAgentDelegateHandle>()
                     : _active.Values)
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (!deferredResourceDisposal)
        {
            DisposeResources();
        }
        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("Delegated agent shutdown encountered one or more failures.", failures);
        }
    }

    private async Task DisposeResourcesWhenDrainedAsync(Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // Shutdown already reported the failure; resource release must still run.
        }
        finally
        {
            DisposeResources();
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _lifetime.Dispose();
        _concurrency.Dispose();
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
            // A timer cancellation callback cannot replace the shutdown outcome.
        }
    }

    private AgentTool CreateDelegateTool(GameAgentExtensionApi api, GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "delegate_agent",
                "Run an isolated delegated agent in the foreground or background.",
                DelegateSchema),
            async (arguments, execution, cancellationToken) =>
            {
                GameAgentDelegationRecord? parent = null;
                if (arguments.TryGetProperty("parentDelegationId", out var configuredParent))
                {
                    var parentId = configuredParent.GetString() ?? string.Empty;
                    parent = await _store.LoadAsync(
                        context.Input.SessionId,
                        context.Input.ActorId,
                        parentId,
                        cancellationToken).ConfigureAwait(false);
                    if (parent is null)
                    {
                        return ToolResult.Error(
                            $"Parent delegation '{parentId}' does not exist.",
                            ToolFailureCategory.InvalidArguments);
                    }

                    EnsureOwner(parent, context);
                }

                var parentDepth = parent?.Depth
                                  ?? (context.Input.Metadata.TryGetValue("agent.delegate_depth", out var depthValue)
                                      && int.TryParse(depthValue, out var parsedDepth)
                                      ? parsedDepth
                                      : 0);
                var depth = checked(parentDepth + 1);
                if (depth > _maximumDepth)
                {
                    return ToolResult.Error(
                        $"Delegation depth {depth} exceeds the configured maximum {_maximumDepth}.",
                        ToolFailureCategory.RuleRejected);
                }

                var id = arguments.TryGetProperty("delegationId", out var configuredId)
                    ? configuredId.GetString() ?? string.Empty
                    : GameExtensionOperationIds.Create(
                        "oga-delegation-v1:",
                        "delegate_agent",
                        context.Input,
                        execution);
                var taskJson = arguments.GetProperty("task").GetRawText();
                var background = arguments.TryGetProperty("background", out var backgroundElement) && backgroundElement.GetBoolean();
                var inheritContext = arguments.TryGetProperty("inheritContext", out var inheritElement) && inheritElement.GetBoolean();
                var maximumTurns = arguments.TryGetProperty("maxTurns", out var maxTurnsElement)
                    ? maxTurnsElement.GetInt32()
                    : 16;
                var parentDelegationId = parent?.Id;
                var rootDelegationId = parent?.RootDelegationId ?? id;
                var key = Key(context, id);
                var existing = await _store.LoadAsync(
                    key.SessionId,
                    key.ActorId,
                    key.Id,
                    cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    EnsureOwner(existing, context);
                    if (!string.Equals(existing.TaskJson, taskJson, StringComparison.Ordinal)
                        || existing.Depth != depth
                        || !string.Equals(existing.ParentDelegationId, parentDelegationId, StringComparison.Ordinal)
                        || !string.Equals(existing.RootDelegationId, rootDelegationId, StringComparison.Ordinal)
                        || existing.Request is null
                        || existing.Request.MaximumTurns != maximumTurns
                        || existing.Request.InheritContext != inheritContext)
                    {
                        return ToolResult.Error(
                            $"Delegation ID '{id}' is already reserved for a different task.",
                            ToolFailureCategory.Conflict);
                    }

                    return JsonResult(existing);
                }

                var request = new GameAgentDelegateRequest(
                    id,
                    context.Input,
                    taskJson,
                    depth,
                    maximumTurns,
                    inheritContext,
                    context.Session.Messages,
                    context.ExecutionScope,
                    parentDelegationId,
                    rootDelegationId);
                var pending = new GameAgentDelegationRecord(
                    id,
                    context.Input.SessionId,
                    context.Input.ActorId,
                    1,
                    GameAgentDelegationStatus.Pending,
                    taskJson,
                    depth,
                    context.Input.Moment,
                    request: request,
                    parentDelegationId: parentDelegationId,
                    rootDelegationId: rootDelegationId);
                var saved = await _store.SaveAsync(pending, 0, cancellationToken).ConfigureAwait(false);
                if (!saved.Saved)
                {
                    if (!string.Equals(saved.Current.TaskJson, pending.TaskJson, StringComparison.Ordinal)
                        || saved.Current.Depth != pending.Depth
                        || saved.Current.CreatedAt != pending.CreatedAt
                        || !string.Equals(saved.Current.ParentDelegationId, pending.ParentDelegationId, StringComparison.Ordinal)
                        || !string.Equals(saved.Current.RootDelegationId, pending.RootDelegationId, StringComparison.Ordinal)
                        || saved.Current.Request is null
                        || saved.Current.Request.MaximumTurns != pending.Request!.MaximumTurns
                        || saved.Current.Request.InheritContext != pending.Request.InheritContext)
                    {
                        return ToolResult.Error(
                            $"Delegation ID '{id}' is already reserved for a different task.",
                            ToolFailureCategory.Conflict);
                    }

                    return JsonResult(saved.Current);
                }

                await api.PublishAsync(DelegationChanged, pending, cancellationToken).ConfigureAwait(false);
                if (background)
                {
                    var task = ScheduleBackground(api, key, pending, request);
                    _ = ObserveAsync(key, task);
                    return JsonResult(new { delegationId = id, status = GameAgentDelegationStatus.Pending, background = true });
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                var completed = await RunAsync(api, pending, request, linked.Token).ConfigureAwait(false);
                return JsonResult(completed);
            },
            ToolRisk.IdempotentWrite,
            ToolExecutionMode.Sequential);

    private Task ScheduleBackground(
        GameAgentExtensionApi api,
        (string SessionId, string ActorId, string Id) key,
        GameAgentDelegationRecord record,
        GameAgentDelegateRequest request)
    {
        lock (_scheduleGate)
        {
            ThrowIfDisposed();
            if (_running.ContainsKey(key))
            {
                throw new InvalidOperationException($"Delegation '{key.Id}' is already scheduled.");
            }

            var task = RunAsync(api, record, request, _lifetime.Token);
            if (!_running.TryAdd(key, task))
            {
                throw new InvalidOperationException($"Delegation '{key.Id}' is already scheduled.");
            }

            return task;
        }
    }

    private AgentTool CreateGetTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition("get_delegate_result", "Get delegated agent status or result by ID.", IdSchema),
            async (arguments, _, cancellationToken) =>
            {
                var id = arguments.GetProperty("delegationId").GetString() ?? string.Empty;
                var key = Key(context, id);
                var record = await _store.LoadAsync(
                    key.SessionId,
                    key.ActorId,
                    key.Id,
                    cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    return ToolResult.Error($"Delegation '{id}' does not exist.", ToolFailureCategory.InvalidArguments);
                }

                return JsonResult(record);
            },
            ToolRisk.ReadOnly);

    private AgentTool CreateListTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition(
                "list_delegations",
                "List a bounded delegation lineage for this actor session without exposing recovery context.",
                ListSchema),
            async (arguments, _, cancellationToken) =>
            {
                var root = arguments.TryGetProperty("rootDelegationId", out var rootElement)
                    ? rootElement.GetString()
                    : null;
                var maximum = arguments.TryGetProperty("maximum", out var maximumElement)
                    ? maximumElement.GetInt32()
                    : 64;
                var records = await _store.ListAsync(
                    context.Input.SessionId,
                    context.Input.ActorId,
                    root,
                    maximum,
                    cancellationToken).ConfigureAwait(false);
                return JsonResult(new { delegations = records });
            },
            ToolRisk.ReadOnly);

    private AgentTool CreateSteerTool(GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition("steer_delegate", "Send a bounded JSON message to a running delegated agent.", SteerSchema),
            async (arguments, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = arguments.GetProperty("delegationId").GetString() ?? string.Empty;
                var key = Key(context, id);
                var record = await _store.LoadAsync(
                    key.SessionId,
                    key.ActorId,
                    key.Id,
                    cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    return ToolResult.Error($"Delegation '{id}' does not exist.", ToolFailureCategory.InvalidArguments);
                }

                if (!_active.TryGetValue(key, out var handle))
                {
                    return ToolResult.Error(
                        $"Delegation '{id}' is not currently running.",
                        ToolFailureCategory.Conflict);
                }

                var accepted = handle.TrySteer(AgentMessage.UserJson(arguments.GetProperty("message").GetRawText()));
                return JsonResult(new { delegationId = id, accepted });
            },
            ToolRisk.NonIdempotentWrite,
            ToolExecutionMode.Sequential);

    private AgentTool CreateCancelTool(GameAgentExtensionApi api, GameAgentExtensionRunContext context) =>
        new(
            new ToolDefinition("cancel_delegate", "Cancel a running delegated agent by ID.", IdSchema),
            async (arguments, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = arguments.GetProperty("delegationId").GetString() ?? string.Empty;
                var key = Key(context, id);
                var record = await _store.LoadAsync(
                    key.SessionId,
                    key.ActorId,
                    key.Id,
                    cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    return ToolResult.Error($"Delegation '{id}' does not exist.", ToolFailureCategory.InvalidArguments);
                }

                var accepted = _active.TryGetValue(key, out var handle) && handle.TryCancel();
                if (!accepted && record.Status == GameAgentDelegationStatus.Pending)
                {
                    var cancelled = WithStatus(
                        record,
                        GameAgentDelegationStatus.Cancelled,
                        record.Revision + 1,
                        leaseId: null,
                        leaseExpiresAt: null);
                    var saved = await _store.SaveAsync(cancelled, record.Revision, cancellationToken).ConfigureAwait(false);
                    accepted = saved.Saved;
                    if (accepted)
                    {
                        await api.PublishAsync(DelegationChanged, cancelled, cancellationToken).ConfigureAwait(false);
                    }
                }

                return JsonResult(new { delegationId = id, accepted });
            },
            ToolRisk.IdempotentWrite,
            ToolExecutionMode.Sequential);

    private async Task<GameAgentDelegationRecord> RunAsync(
        GameAgentExtensionApi api,
        GameAgentDelegationRecord pending,
        GameAgentDelegateRequest request,
        CancellationToken cancellationToken)
    {
        var current = pending;
        var key = (pending.SessionId, pending.ActorId, pending.Id);
        var concurrencyAcquired = false;
        GameAgentDelegationStatus finalStatus;
        string? resultJson;
        string? error;
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            concurrencyAcquired = true;
            var now = DateTimeOffset.UtcNow;
            if (!pending.IsRecoverable(now))
            {
                return pending;
            }

            var leaseId = string.Concat(_workerId, ":", Guid.NewGuid().ToString("N"));
            var running = WithStatus(
                pending,
                GameAgentDelegationStatus.Running,
                pending.Revision + 1,
                leaseId,
                now + _leaseDuration,
                checked(pending.Attempt + 1));
            var runningSave = await _store.SaveAsync(running, pending.Revision, cancellationToken).ConfigureAwait(false);
            if (!runningSave.Saved)
            {
                return runningSave.Current;
            }

            current = running;
            await api.PublishAsync(DelegationChanged, running, cancellationToken).ConfigureAwait(false);
            var executionRequest = request.WithLeaseValidator(
                token => ValidateLeaseAsync(key, leaseId, token));
            using var handle = _executor.Start(executionRequest, cancellationToken)
                ?? throw new InvalidOperationException("The delegate executor returned null.");
            if (!_active.TryAdd(key, handle))
            {
                throw new InvalidOperationException($"Delegation '{request.Id}' is already active.");
            }

            // Shutdown can race the small interval between Start and publishing the
            // handle in _active. Re-check the lifetime after publication so a handle
            // that missed the DisposeAsync snapshot is still asked to stop.
            if (_lifetime.IsCancellationRequested)
            {
                handle.TryCancel();
            }

            GameAgentDelegateOutcome outcome;
            try
            {
                while (!handle.Completion.IsCompleted)
                {
                    var renewAfter = TimeSpan.FromMilliseconds(Math.Max(250, _leaseDuration.TotalMilliseconds / 2));
                    var renewal = Task.Delay(renewAfter, cancellationToken);
                    var winner = await Task.WhenAny(handle.Completion, renewal).ConfigureAwait(false);
                    if (ReferenceEquals(winner, handle.Completion))
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var renewed = WithStatus(
                        current,
                        GameAgentDelegationStatus.Running,
                        current.Revision + 1,
                        leaseId,
                        DateTimeOffset.UtcNow + _leaseDuration,
                        current.Attempt);
                    var renewalSave = await _store.SaveAsync(renewed, current.Revision, cancellationToken).ConfigureAwait(false);
                    if (!renewalSave.Saved)
                    {
                        handle.TryCancel();
                        return renewalSave.Current;
                    }

                    current = renewed;
                }

                outcome = await handle.Completion.ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The delegated agent returned null.");
            }
            finally
            {
                _active.TryRemove(key, out _);
            }

            if (_lifetime.IsCancellationRequested)
            {
                return current;
            }

            var status = outcome.Succeeded
                ? GameAgentDelegationStatus.Completed
                : outcome.Cancelled || cancellationToken.IsCancellationRequested
                    ? GameAgentDelegationStatus.Cancelled
                    : GameAgentDelegationStatus.Failed;
            finalStatus = status;
            resultJson = SerializeOutcome(outcome.Messages, _maximumResultCharacters);
            error = Bound(outcome.Error, _maximumResultCharacters);
        }
        catch (OperationCanceledException)
        {
            if (_lifetime.IsCancellationRequested)
            {
                return current;
            }

            finalStatus = GameAgentDelegationStatus.Cancelled;
            resultJson = null;
            error = "The delegation was cancelled before execution completed.";
        }
        catch (Exception exception)
        {
            finalStatus = GameAgentDelegationStatus.Failed;
            resultJson = null;
            error = Bound(exception.Message, _maximumResultCharacters);
        }
        finally
        {
            if (concurrencyAcquired)
            {
                _concurrency.Release();
            }
        }

        return await FinishAsync(api, current, finalStatus, resultJson, error).ConfigureAwait(false);
    }

    private async Task<GameAgentDelegationRecord> FinishAsync(
        GameAgentExtensionApi api,
        GameAgentDelegationRecord current,
        GameAgentDelegationStatus status,
        string? resultJson,
        string? error)
    {
        if (IsTerminal(current.Status))
        {
            return current;
        }

        var final = new GameAgentDelegationRecord(
            current.Id,
            current.SessionId,
            current.ActorId,
            current.Revision + 1,
            status,
            current.TaskJson,
            current.Depth,
            current.CreatedAt,
            resultJson,
            error,
            current.Request,
            current.ParentDelegationId,
            current.RootDelegationId,
            leaseId: null,
            leaseExpiresAt: null,
            attempt: current.Attempt);
        using var settlement = new CancellationTokenSource(_settlementTimeout);
        GameAgentDelegationSaveResult save;
        try
        {
            save = await _store.SaveAsync(final, current.Revision, settlement.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (settlement.IsCancellationRequested)
        {
            throw new TimeoutException("Delegation status settlement exceeded its configured timeout.", exception);
        }

        var saved = save.Saved ? final : save.Current;
        try
        {
            await api.PublishAsync(DelegationChanged, saved, settlement.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (settlement.IsCancellationRequested)
        {
            // The durable terminal record is authoritative; a slow observer cannot undo it.
        }

        return saved;
    }

    private static bool IsTerminal(GameAgentDelegationStatus status) =>
        status == GameAgentDelegationStatus.Completed
        || status == GameAgentDelegationStatus.Failed
        || status == GameAgentDelegationStatus.Cancelled;

    private async Task ObserveAsync((string SessionId, string ActorId, string Id) key, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The task remains visible through _running until this observer reaches its finally block.
            // Observing the exception prevents an abandoned background fault from surfacing globally.
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private static (string SessionId, string ActorId, string Id) Key(
        GameAgentExtensionRunContext context,
        string id) =>
        (context.Input.SessionId, context.Input.ActorId, id);

    private async ValueTask<bool> ValidateLeaseAsync(
        (string SessionId, string ActorId, string Id) key,
        string leaseId,
        CancellationToken cancellationToken)
    {
        var current = await _store.LoadAsync(
            key.SessionId,
            key.ActorId,
            key.Id,
            cancellationToken).ConfigureAwait(false);
        return current is not null
               && current.Status == GameAgentDelegationStatus.Running
               && string.Equals(current.LeaseId, leaseId, StringComparison.Ordinal)
               && current.LeaseExpiresAt > DateTimeOffset.UtcNow;
    }

    private static GameAgentDelegationRecord WithStatus(
        GameAgentDelegationRecord current,
        GameAgentDelegationStatus status,
        long revision,
        string? leaseId = null,
        DateTimeOffset? leaseExpiresAt = null,
        int? attempt = null) =>
        new(
            current.Id,
            current.SessionId,
            current.ActorId,
            revision,
            status,
            current.TaskJson,
            current.Depth,
            current.CreatedAt,
            current.ResultJson,
            current.Error,
            current.Request,
            current.ParentDelegationId,
            current.RootDelegationId,
            leaseId,
            leaseExpiresAt,
            attempt ?? current.Attempt);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AgentDelegationExtension));
        }
    }

    private static void EnsureOwner(GameAgentDelegationRecord record, GameAgentExtensionRunContext context)
    {
        if (!string.Equals(record.SessionId, context.Input.SessionId, StringComparison.Ordinal)
            || !string.Equals(record.ActorId, context.Input.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A delegation record belongs to another actor session.");
        }
    }

    private static string SerializeOutcome(IReadOnlyList<AgentMessage> messages, int maximumCharacters)
    {
        var projected = new List<object>();
        var remainingStringBudget = Math.Max(128, maximumCharacters / 8);
        var truncated = false;
        foreach (var message in messages)
        {
            if (remainingStringBudget <= 0 || projected.Count >= 512)
            {
                truncated = true;
                break;
            }

            var content = new List<object>();
            foreach (var part in message.Content)
            {
                if (remainingStringBudget <= 0 || content.Count >= 256)
                {
                    truncated = true;
                    break;
                }

                content.Add(ContentValue(part, ref remainingStringBudget));
            }

            projected.Add(new { role = message.Role.ToString(), content });
        }

        string serialized;
        do
        {
            serialized = JsonSerializer.Serialize(new { messages = projected, truncated });
            if (serialized.Length <= maximumCharacters || projected.Count == 0)
            {
                return serialized;
            }

            projected.RemoveAt(projected.Count - 1);
            truncated = true;
        }
        while (true);
    }

    private static object ContentValue(AgentContent content, ref int remainingStringBudget) => content switch
    {
        TextContent text => new { type = "text", text = Take(text.Text, ref remainingStringBudget) },
        JsonContent json => new { type = "json", json = Take(json.Json, ref remainingStringBudget) },
        ReasoningContent => new { type = "reasoning", omitted = true },
        ResourceContent resource => new
        {
            type = "resource",
            uri = Take(resource.Uri, ref remainingStringBudget),
            mediaType = Take(resource.MediaType, ref remainingStringBudget),
        },
        ToolCallContent call => new
        {
            type = "toolCall",
            id = Take(call.Id, ref remainingStringBudget),
            name = Take(call.Name, ref remainingStringBudget),
            arguments = Take(call.ArgumentsJson, ref remainingStringBudget),
        },
        _ => new { type = "unknown" },
    };

    private static string Take(string value, ref int remaining)
    {
        var count = Math.Min(value.Length, remaining);
        if (count > 0 && count < value.Length && char.IsHighSurrogate(value[count - 1]))
        {
            count--;
        }

        remaining -= count;
        return count == value.Length ? value : value.Substring(0, count);
    }

    private static string? Bound(string? value, int maximumCharacters)
    {
        if (value is null || value.Length <= maximumCharacters)
        {
            return value;
        }

        var length = maximumCharacters;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value.Substring(0, length);
    }

    private static ToolResult JsonResult(object value) =>
        new(new AgentContent[] { new JsonContent(JsonSerializer.Serialize(value)) });
}
