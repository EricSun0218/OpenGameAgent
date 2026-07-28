using System.Collections.Concurrent;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class RuntimeControlPlane
{
    private readonly ConcurrentDictionary<string, ActiveRunControl> _active =
        new(StringComparer.Ordinal);
    private readonly RunControlMailboxOptions _mailboxOptions;
    private readonly BoundedCancellationDispatcher _cancellationDispatcher;

    public RuntimeControlPlane(RunControlMailboxOptions? mailboxOptions = null)
        : this(
            mailboxOptions,
            BoundedCancellationDispatcher.Shared)
    {
    }

    internal RuntimeControlPlane(
        RunControlMailboxOptions? mailboxOptions,
        BoundedCancellationDispatcher cancellationDispatcher)
    {
        _mailboxOptions = mailboxOptions ?? new RunControlMailboxOptions();
        _cancellationDispatcher = cancellationDispatcher
                                  ?? throw new ArgumentNullException(
                                      nameof(cancellationDispatcher));
    }

    public bool TryPost(string runId, RunControlCommand command)
    {
        RuntimeGuard.RequiredId(runId, nameof(runId));
        var snapshot = ValidateAndSnapshot(
            command,
            _mailboxOptions);
        return _active.TryGetValue(runId, out var control)
               && control.Post(snapshot);
    }

    internal Registration Register(string runId, string? worldId = null)
    {
        runId = RuntimeGuard.RequiredId(runId, nameof(runId));
        if (worldId is not null)
        {
            worldId = RuntimeGuard.RequiredId(worldId, nameof(worldId));
        }

        var active = new ActiveRunControl(
            _mailboxOptions,
            worldId,
            _cancellationDispatcher);
        if (!_active.TryAdd(runId, active))
        {
            throw new DuplicateRunException(runId);
        }

        return new Registration(this, runId, active);
    }

    private static RunControlCommand ValidateAndSnapshot(
        RunControlCommand command,
        RunControlMailboxOptions mailboxOptions)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        RuntimeGuard.RequiredId(command.CommandId, nameof(command));

        var carriesObservation =
            string.Equals(command.Kind, RunControlKinds.Steer, StringComparison.Ordinal)
            || string.Equals(
                command.Kind,
                RunControlKinds.FollowUp,
                StringComparison.Ordinal);
        if (!carriesObservation
            && !string.Equals(
                command.Kind,
                RunControlKinds.Interrupt,
                StringComparison.Ordinal)
            && !string.Equals(
                command.Kind,
                RunControlKinds.Cancel,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Control command kind is unsupported.",
                nameof(command));
        }

        ObservationEnvelope? observation = null;
        if (carriesObservation)
        {
            if (command.Observation is null)
            {
                throw new ArgumentException(
                    "Steer and follow-up commands require an observation.",
                    nameof(command));
            }

            var limits = new JsonValueLimits(
                maxUtf8Bytes: mailboxOptions.MaxObservationUtf8Bytes,
                maxStringUtf8Bytes: Math.Min(
                    mailboxOptions.MaxObservationUtf8Bytes,
                    65_536));
            observation = RuntimeProtocolInputGuard
                .ValidateObservationBeforeSerialization(
                    command.Observation,
                    limits,
                    mailboxOptions.MaxObservationUtf8Bytes,
                    nameof(command),
                    maximumExtensionItems: 256,
                    byteLimitCode: "control_observation_bytes_exceeded",
                    itemLimitCode: "control_observation_items_exceeded");
        }
        else if (command.Observation is not null)
        {
            throw new ArgumentException(
                "Cancel and interrupt commands cannot carry an observation.",
                nameof(command));
        }

        return new RunControlCommand
        {
            CommandId = command.CommandId,
            Kind = command.Kind,
            Observation = observation,
            CreatedAt = command.CreatedAt
        };
    }

    internal sealed class Registration : IDisposable
    {
        private RuntimeControlPlane? _owner;
        private readonly string _runId;
        private readonly ActiveRunControl _control;

        public Registration(
            RuntimeControlPlane owner,
            string runId,
            ActiveRunControl control)
        {
            _owner = owner;
            _runId = runId;
            _control = control;
        }

        public StepLease BeginStep(CancellationToken cancellationToken)
        {
            return _control.BeginStep(cancellationToken);
        }

        public IReadOnlyList<RunControlCommand> Drain()
        {
            return _control.Drain();
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            if (owner._active.TryGetValue(_runId, out var current)
                && ReferenceEquals(current, _control))
            {
                owner._active.TryRemove(_runId, out _);
            }
            _control.Dispose();
        }
    }

    internal sealed class StepLease : IDisposable
    {
        private ActiveRunControl? _owner;

        public StepLease(
            ActiveRunControl owner,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndStep();
        }
    }

    internal sealed class ActiveRunControl : IDisposable
    {
        private readonly object _sync = new();
        private readonly RunControlMailbox _mailbox;
        private readonly BoundedCancellationDispatcher
            _cancellationDispatcher;
        private CancellationTokenSource? _step;
        private Task? _stepCancellation;
        private BoundedCancellationDispatcher.CancellationDispatchReservation?
            _stepCancellationReservation;
        private readonly string? _worldId;
        private bool _disposed;

        public ActiveRunControl(
            RunControlMailboxOptions mailboxOptions,
            string? worldId,
            BoundedCancellationDispatcher cancellationDispatcher)
        {
            _mailbox = new RunControlMailbox(mailboxOptions);
            _worldId = worldId;
            _cancellationDispatcher = cancellationDispatcher;
        }

        public bool Post(RunControlCommand command)
        {
            var accepted = false;
            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_worldId is not null
                    && command.Observation is not null
                    && !string.Equals(
                        command.Observation.WorldId,
                        _worldId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                if (!_mailbox.TryPost(command))
                {
                    return false;
                }

                accepted = true;
                if (!string.Equals(
                        command.Kind,
                        RunControlKinds.FollowUp,
                        StringComparison.Ordinal)
                    && _step is not null
                    && _stepCancellation is null)
                {
                    _stepCancellation =
                        _stepCancellationReservation!
                            .DispatchAsync(_step);
                }
            }

            return accepted;
        }

        public StepLease BeginStep(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(ActiveRunControl));
                }

                if (_step is not null)
                {
                    throw new InvalidOperationException(
                        "A run cannot attach more than one active step.");
                }

                if (!_cancellationDispatcher.TryReserve(
                        out var cancellationReservation))
                {
                    throw new InvalidOperationException(
                        "Run-control cancellation capacity is exhausted.");
                }

                try
                {
                    _step = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    _stepCancellation = null;
                    _stepCancellationReservation = cancellationReservation;
                    return new StepLease(this, _step.Token);
                }
                catch
                {
                    cancellationReservation!.Dispose();
                    throw;
                }
            }
        }

        public IReadOnlyList<RunControlCommand> Drain()
        {
            var commands = new List<RunControlCommand>();
            while (_mailbox.TryRead(out var command))
            {
                commands.Add(command!);
            }

            return commands;
        }

        public void EndStep()
        {
            CancellationTokenSource? step;
            Task? cancellation;
            BoundedCancellationDispatcher.CancellationDispatchReservation?
                cancellationReservation;
            lock (_sync)
            {
                step = _step;
                cancellation = _stepCancellation;
                cancellationReservation = _stepCancellationReservation;
                _step = null;
                _stepCancellation = null;
                _stepCancellationReservation = null;
            }

            if (step is null)
            {
                return;
            }

            if (cancellation is null)
            {
                try
                {
                    step.Dispose();
                }
                finally
                {
                    cancellationReservation!.Dispose();
                }
            }
            else
            {
                _ = DisposeAfterCancellationAsync(
                    step,
                    cancellation,
                    cancellationReservation!);
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? step;
            Task? cancellation;
            BoundedCancellationDispatcher.CancellationDispatchReservation?
                cancellationReservation;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                step = _step;
                cancellation = _stepCancellation;
                cancellationReservation = _stepCancellationReservation;
                _step = null;
                _stepCancellation = null;
                _stepCancellationReservation = null;
                _mailbox.Dispose();
            }

            if (step is null)
            {
                return;
            }

            cancellation ??=
                cancellationReservation!.DispatchAsync(step);
            _ = DisposeAfterCancellationAsync(
                step,
                cancellation,
                cancellationReservation!);
        }

        private static async Task DisposeAfterCancellationAsync(
            CancellationTokenSource cancellation,
            Task cancellationTask,
            BoundedCancellationDispatcher.CancellationDispatchReservation
                cancellationReservation)
        {
            try
            {
                await cancellationTask.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    cancellation.Dispose();
                }
                finally
                {
                    cancellationReservation.Dispose();
                }
            }
        }
    }
}
