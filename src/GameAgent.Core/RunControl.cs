using System.Text;
using GameAgent.Protocol;

namespace GameAgent.Core;

public static class RunControlKinds
{
    public const string Steer = "steer";
    public const string FollowUp = "follow_up";
    public const string Interrupt = "interrupt";
    public const string Cancel = "cancel";
}

public sealed class RunControlCommand
{
    public string CommandId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public ObservationEnvelope? Observation { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RunControlMailboxOptions
{
    public RunControlMailboxOptions(
        int maxCommands = 64,
        int maxObservationUtf8Bytes = 262_144,
        int maxBufferedObservationUtf8Bytes = 1_048_576,
        int maxRememberedCommandIds = 256)
    {
        if (maxCommands < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCommands));
        }

        if (maxObservationUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxObservationUtf8Bytes));
        }

        if (maxBufferedObservationUtf8Bytes < maxObservationUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBufferedObservationUtf8Bytes));
        }

        if (maxRememberedCommandIds < maxCommands)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRememberedCommandIds));
        }

        MaxCommands = maxCommands;
        MaxObservationUtf8Bytes = maxObservationUtf8Bytes;
        MaxBufferedObservationUtf8Bytes =
            maxBufferedObservationUtf8Bytes;
        MaxRememberedCommandIds = maxRememberedCommandIds;
    }

    public int MaxCommands { get; }

    public int MaxObservationUtf8Bytes { get; }

    public int MaxBufferedObservationUtf8Bytes { get; }

    public int MaxRememberedCommandIds { get; }
}

public sealed class RunControlMailbox : IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<BufferedCommand> _commands = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _disposedSignal = new();
    private readonly HashSet<string> _rememberedIds =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedOrder = new();
    private readonly RunControlMailboxOptions _options;
    private int _bufferedObservationBytes;
    private bool _disposed;

    public RunControlMailbox(RunControlMailboxOptions? options = null)
    {
        _options = options ?? new RunControlMailboxOptions();
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _commands.Count;
            }
        }
    }

    public void Post(RunControlCommand command)
    {
        if (!TryPost(command))
        {
            throw new InvalidOperationException(
                "The control mailbox rejected a duplicate or over-capacity command.");
        }
    }

    public bool TryPost(RunControlCommand command)
    {
        var buffered = Snapshot(command);
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RunControlMailbox));
            }

            if (_rememberedIds.Contains(buffered.Command.CommandId))
            {
                return false;
            }

            var projectedBytes =
                _bufferedObservationBytes + buffered.ObservationUtf8Bytes;
            var atCapacity = _commands.Count >= _options.MaxCommands;
            if (atCapacity
                || projectedBytes
                > _options.MaxBufferedObservationUtf8Bytes)
            {
                var replacement = FindReplacement(buffered);
                if (replacement is null)
                {
                    return false;
                }

                _bufferedObservationBytes -=
                    replacement.Value.ObservationUtf8Bytes;
                _commands.Remove(replacement);
                _commands.AddFirst(buffered);
            }
            else
            {
                _commands.AddLast(buffered);
                _available.Release();
            }

            _bufferedObservationBytes += buffered.ObservationUtf8Bytes;
            Remember(buffered.Command.CommandId);
            return true;
        }
    }

    public bool TryRead(out RunControlCommand? command)
    {
        if (!_available.Wait(0))
        {
            command = null;
            return false;
        }

        lock (_sync)
        {
            if (_commands.First is null)
            {
                command = null;
                return false;
            }

            var buffered = _commands.First.Value;
            _commands.RemoveFirst();
            _bufferedObservationBytes -= buffered.ObservationUtf8Bytes;
            command = buffered.Command;
            return true;
        }
    }

    public async ValueTask<RunControlCommand> ReadAsync(
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposedSignal.Token);
        try
        {
            await _available.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested
                  && _disposedSignal.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(RunControlMailbox));
        }

        lock (_sync)
        {
            if (_commands.First is not null)
            {
                var buffered = _commands.First.Value;
                _commands.RemoveFirst();
                _bufferedObservationBytes -= buffered.ObservationUtf8Bytes;
                return buffered.Command;
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RunControlMailbox));
            }
        }

        throw new InvalidOperationException("The control signal had no queued command.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _commands.Clear();
            _bufferedObservationBytes = 0;
            _disposedSignal.Cancel();
        }
    }

    private LinkedListNode<BufferedCommand>? FindReplacement(
        BufferedCommand incoming)
    {
        LinkedListNode<BufferedCommand>? selected = null;
        for (var node = _commands.Last; node is not null; node = node.Previous)
        {
            if (node.Value.Priority >= incoming.Priority)
            {
                continue;
            }

            var resultingBytes = _bufferedObservationBytes
                                 - node.Value.ObservationUtf8Bytes
                                 + incoming.ObservationUtf8Bytes;
            if (resultingBytes
                > _options.MaxBufferedObservationUtf8Bytes)
            {
                continue;
            }

            if (selected is null
                || node.Value.Priority < selected.Value.Priority)
            {
                selected = node;
            }
        }

        return selected;
    }

    private void Remember(string commandId)
    {
        _rememberedIds.Add(commandId);
        _rememberedOrder.Enqueue(commandId);
        while (_rememberedOrder.Count > _options.MaxRememberedCommandIds)
        {
            _rememberedIds.Remove(_rememberedOrder.Dequeue());
        }
    }

    private BufferedCommand Snapshot(RunControlCommand command)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        ObservationEnvelope? observation = null;
        var observationBytes = 0;
        if (command.Observation is not null)
        {
            var json = ProtocolJson.Serialize(command.Observation);
            observationBytes = Encoding.UTF8.GetByteCount(json);
            if (observationBytes > _options.MaxObservationUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(command),
                    "control_observation_bytes_exceeded",
                    "The control observation exceeds the mailbox limit.");
            }

            observation =
                ProtocolJson.DeserializeObservationEnvelope(json);
        }

        return new BufferedCommand(
            new RunControlCommand
            {
                CommandId = command.CommandId,
                Kind = command.Kind,
                Observation = observation,
                CreatedAt = command.CreatedAt
            },
            observationBytes,
            Priority(command.Kind));
    }

    private static int Priority(string kind)
    {
        if (string.Equals(kind, RunControlKinds.Cancel, StringComparison.Ordinal))
        {
            return 3;
        }

        if (string.Equals(
                kind,
                RunControlKinds.Interrupt,
                StringComparison.Ordinal))
        {
            return 2;
        }

        return string.Equals(
            kind,
            RunControlKinds.Steer,
            StringComparison.Ordinal)
            ? 1
            : 0;
    }

    private sealed class BufferedCommand
    {
        public BufferedCommand(
            RunControlCommand command,
            int observationUtf8Bytes,
            int priority)
        {
            Command = command;
            ObservationUtf8Bytes = observationUtf8Bytes;
            Priority = priority;
        }

        public RunControlCommand Command { get; }

        public int ObservationUtf8Bytes { get; }

        public int Priority { get; }
    }
}
