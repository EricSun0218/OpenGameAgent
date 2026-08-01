using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Core;

/// <summary>
/// Treats a custom context engine as an untrusted in-process extension. Calls
/// are isolated behind bounded admission and deadlines, while returned views
/// are remeasured and stable high-authority messages are admitted by the
/// runtime rather than by the extension.
/// </summary>
internal sealed class BoundedConversationContextEngine :
    IConversationContextEngine
{
    private readonly IConversationContextEngine _inner;
    private readonly ConversationContextOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, Task> _detached = new();
    private readonly object _lifecycleSync = new();
    private readonly string _engineId;
    private readonly string _version;
    private TaskCompletionSource<bool>? _idle;
    private Task<bool>? _cleanupTask;
    private long _nextDetachedId;
    private int _activeCalls;
    private int _closed;
    private int _resourcesDisposed;

    public BoundedConversationContextEngine(
        IConversationContextEngine inner,
        ConversationContextOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = (options ?? throw new ArgumentNullException(nameof(options)))
            .Snapshot();
        try
        {
            _engineId = RuntimeGuard.RequiredUtf8(
                inner.EngineId,
                128,
                nameof(inner));
            _version = RuntimeGuard.RequiredUtf8(
                inner.Version,
                64,
                nameof(inner));
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new ArgumentException(
                "The conversation context engine identity is invalid.",
                nameof(inner),
                exception);
        }

        _slots = new SemaphoreSlim(
            _options.MaxConcurrentCompactions,
            _options.MaxConcurrentCompactions);
    }

    public string EngineId => _engineId;

    public string Version => _version;

    public bool CleanupCompleted =>
        Volatile.Read(ref _resourcesDisposed) != 0;

    public async ValueTask<ConversationContextView> PrepareAsync(
        string runId,
        string turnId,
        IReadOnlyList<NormalizedMessage> transcript,
        IReadOnlyCollection<string>? stablePrefixMessageIds = null,
        CancellationToken cancellationToken = default)
    {
        runId = RuntimeGuard.RequiredUtf8(runId, 128, nameof(runId));
        turnId = RuntimeGuard.RequiredUtf8(turnId, 128, nameof(turnId));
        var input = ConversationContextManager.SnapshotCompactionMessages(
            transcript,
            _options);
        _ = RuntimePromptBuilder.MeasurePrompt(
            input,
            Array.Empty<GameAgent.Protocol.ToolDescriptor>(),
            _options.MaxInputMessages,
            _options.MaxInputUtf8Bytes,
            estimatedBytesPerToken: 4);
        var stableIds =
            ConversationContextManager.SnapshotStablePrefixMessageIds(
                stablePrefixMessageIds,
                input,
                _options);
        EnterCall();
        try
        {
            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken,
                    _shutdown.Token);
            var entered = await _slots.WaitAsync(
                    _options.CompactionTimeout,
                    linked.Token)
                .ConfigureAwait(false);
            if (!entered)
            {
                throw new TimeoutException(
                    "Conversation context engine capacity was exhausted.");
            }

            var deadline = CancellationTokenSource
                .CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(_options.CompactionTimeout);
            Task<ConversationContextView> operation;
            try
            {
                operation = Task.Run(
                    async () => await _inner.PrepareAsync(
                            runId,
                            turnId,
                            input,
                            stableIds,
                            deadline.Token)
                        .ConfigureAwait(false));
            }
            catch
            {
                deadline.Dispose();
                _slots.Release();
                throw;
            }

            var timeout = Task.Delay(_options.CompactionTimeout);
            var cancelled = Task.Delay(
                Timeout.InfiniteTimeSpan,
                linked.Token);
            var completed = await Task.WhenAny(
                    operation,
                    timeout,
                    cancelled)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, operation))
            {
                TrackDetached(operation, deadline);
                linked.Token.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    "Conversation context engine exceeded its deadline.");
            }

            deadline.Dispose();
            _slots.Release();
            var view = await operation.ConfigureAwait(false)
                       ?? throw new InvalidDataException(
                           "The conversation context engine returned no view.");
            return AdmitView(
                runId,
                turnId,
                input,
                stableIds,
                view,
                cancellationToken);
        }
        finally
        {
            ExitCall();
        }
    }

    public void RegisterCheckpoint(JsonElement checkpoint)
    {
        JsonValueInspector.ValidateAndMeasure(
            checkpoint,
            new JsonValueLimits(
                maxUtf8Bytes: _options.MaxInputUtf8Bytes,
                maxDepth: 64,
                maxNodes: _options.MaxInputJsonNodes,
                maxStringUtf8Bytes: 1_048_576,
                maxContainerItems: 65_536),
            nameof(checkpoint));
        var snapshot = checkpoint.Clone();
        EnterCall();
        try
        {
            if (!_slots.Wait(_options.CompactionTimeout, _shutdown.Token))
            {
                throw new TimeoutException(
                    "Conversation context engine capacity was exhausted.");
            }

            var deadline = CancellationTokenSource
                .CreateLinkedTokenSource(_shutdown.Token);
            deadline.CancelAfter(_options.CompactionTimeout);
            Task operation;
            try
            {
                operation = Task.Run(
                    () => _inner.RegisterCheckpoint(snapshot));
            }
            catch
            {
                deadline.Dispose();
                _slots.Release();
                throw;
            }

            var detached = false;
            try
            {
                var completed = Task.WhenAny(
                        operation,
                        Task.Delay(_options.CompactionTimeout))
                    .GetAwaiter()
                    .GetResult();
                if (!ReferenceEquals(completed, operation))
                {
                    TrackDetached(operation, deadline);
                    detached = true;
                    throw new TimeoutException(
                        "Conversation context checkpoint registration exceeded its deadline.");
                }

                operation.GetAwaiter().GetResult();
            }
            finally
            {
                if (!detached)
                {
                    deadline.Dispose();
                    _slots.Release();
                }
            }
        }
        finally
        {
            ExitCall();
        }
    }

    public async ValueTask<bool> StopAsync()
    {
        Task<bool> cleanup;
        lock (_lifecycleSync)
        {
            if (_cleanupTask is null
                || (_cleanupTask.IsCompletedSuccessfully
                    && !_cleanupTask.Result))
            {
                Volatile.Write(ref _closed, 1);
                _cleanupTask = CompleteCleanupAttemptAsync();
            }

            cleanup = _cleanupTask;
        }

        var completed = await Task.WhenAny(
                cleanup,
                Task.Delay(_options.DetachedShutdownTimeout))
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, cleanup))
        {
            return false;
        }

        return await cleanup.ConfigureAwait(false);
    }

    private async Task<bool> CompleteCleanupAttemptAsync()
    {
        var cancellation = Task.Run(
            () =>
            {
                try
                {
                    _shutdown.Cancel();
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException
                          and not StackOverflowException)
                {
                    // Extension cancellation callbacks cannot own shutdown.
                }
            });
        Task idle;
        lock (_lifecycleSync)
        {
            idle = _activeCalls == 0 && _detached.IsEmpty
                ? Task.CompletedTask
                : (_idle ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await Task.WhenAll(cancellation, idle).ConfigureAwait(false);
        if (!await RunInnerStopAsync().ConfigureAwait(false))
        {
            return false;
        }

        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _slots.Dispose();
            _shutdown.Dispose();
        }

        return true;
    }

    private async Task<bool> RunInnerStopAsync()
    {
        try
        {
            return await Task.Run(
                    async () => await _inner.StopAsync()
                        .ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            return false;
        }
    }

    private ConversationContextView AdmitView(
        string runId,
        string turnId,
        IReadOnlyList<NormalizedMessage> input,
        IReadOnlyList<string> stableIds,
        ConversationContextView view,
        CancellationToken cancellationToken)
    {
        var outputOptions = _options.Snapshot();
        outputOptions.MaxInputMessages = outputOptions.MaxRequestMessages;
        outputOptions.MaxInputUtf8Bytes = outputOptions.MaxRequestUtf8Bytes;
        var output = ConversationContextManager.SnapshotCompactionMessages(
            view.Messages,
            outputOptions);
        if (output.Count > input.Count)
        {
            throw new InvalidDataException(
                "A conversation context view cannot add more messages than its input.");
        }

        var inputById = input.ToDictionary(
            message => message.MessageId,
            message => message,
            StringComparer.Ordinal);
        var stable = new HashSet<string>(stableIds, StringComparer.Ordinal);
        var required = input
            .Where(message =>
                stable.Contains(message.MessageId)
                || string.Equals(
                    message.Role,
                    NormalizedRoles.System,
                    StringComparison.Ordinal))
            .Select(message => message.MessageId)
            .ToArray();
        var requiredSet = new HashSet<string>(
            required,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var returnedRequired = new List<string>(required.Length);
        var retainedInput = 0;
        var generatedSummaries = 0;
        NormalizedMessage? generatedSummary = null;
        foreach (var message in output)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(message.MessageId))
            {
                throw new InvalidDataException(
                    "The conversation context engine returned duplicate message ids.");
            }

            if (inputById.TryGetValue(message.MessageId, out var original))
            {
                retainedInput++;
                if (!string.Equals(
                        NormalizedMessageJournalCodec.EncodeText(message),
                        NormalizedMessageJournalCodec.EncodeText(original),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The conversation context engine altered an admitted message.");
                }
            }
            else
            {
                generatedSummaries++;
                if (generatedSummaries > 1)
                {
                    throw new InvalidDataException(
                        "The conversation context engine introduced multiple summaries.");
                }

                generatedSummary = message;
            }

            if (requiredSet.Contains(message.MessageId))
            {
                returnedRequired.Add(message.MessageId);
            }
        }

        if (!required.SequenceEqual(returnedRequired, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The conversation context engine removed or reordered required messages.");
        }

        if (generatedSummary is not null)
        {
            var retainedIds = output
                .Where(message => inputById.ContainsKey(message.MessageId))
                .Select(message => message.MessageId)
                .ToHashSet(StringComparer.Ordinal);
            var omitted = input
                .Where(message => !retainedIds.Contains(message.MessageId))
                .ToArray();
            ConversationContextManager.ValidateDerivedSummary(
                runId,
                turnId,
                omitted,
                generatedSummary,
                _options,
                cancellationToken);
        }

        var inputBytes = ConversationContextManager.Measure(input);
        var outputBytes = ConversationContextManager.Measure(output);
        var compacted = input.Count != output.Count
                        || !input.Select(message => message.MessageId)
                            .SequenceEqual(
                                output.Select(message => message.MessageId),
                                StringComparer.Ordinal);
        return new ConversationContextView(
            output,
            new ConversationContextReport(
                input.Count,
                output.Count,
                input.Count - retainedInput,
                inputBytes,
                outputBytes,
            compacted,
                compactionFailed: false,
                compactionSkippedByCooldown: false,
                ConversationContextManager.Digest(input),
                ConversationContextManager.Digest(output)));
    }

    private void TrackDetached(Task operation, CancellationTokenSource deadline)
    {
        long id;
        TaskCompletionSource<bool> start;
        Task cleanup;
        do
        {
            id = Interlocked.Increment(ref _nextDetachedId);
            start = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup = CompleteDetachedAsync(
                id,
                operation,
                deadline,
                start.Task);
        }
        while (!_detached.TryAdd(id, cleanup));

        start.TrySetResult(true);
        _ = cleanup.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task CompleteDetachedAsync(
        long id,
        Task operation,
        CancellationTokenSource deadline,
        Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        finally
        {
            deadline.Dispose();
            _slots.Release();
            _detached.TryRemove(id, out _);
            PulseIdle();
        }
    }

    private void EnterCall()
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(BoundedConversationContextEngine));
            }

            _activeCalls = checked(_activeCalls + 1);
        }
    }

    private void ExitCall()
    {
        lock (_lifecycleSync)
        {
            _activeCalls--;
            PulseIdleLocked();
        }
    }

    private void PulseIdle()
    {
        lock (_lifecycleSync)
        {
            PulseIdleLocked();
        }
    }

    private void PulseIdleLocked()
    {
        if (_activeCalls == 0 && _detached.IsEmpty)
        {
            _idle?.TrySetResult(true);
        }
    }
}
