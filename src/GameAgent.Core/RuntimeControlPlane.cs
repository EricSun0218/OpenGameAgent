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
        return TryPost(runId, command, out _);
    }

    public bool TryPost(
        string runId,
        RunControlCommand command,
        out string? rejectionReason)
    {
        RuntimeGuard.RequiredId(runId, nameof(runId));
        var snapshot = ValidateAndSnapshot(
            command,
            _mailboxOptions);
        if (!_active.TryGetValue(runId, out var control))
        {
            rejectionReason = null;
            return false;
        }

        return control.Post(snapshot, out rejectionReason);
    }

    internal Registration Register(string runId, string? worldId = null)
    {
        runId = RuntimeGuard.RequiredId(runId, nameof(runId));
        if (worldId is not null)
        {
            worldId = RuntimeGuard.RequiredId(worldId, nameof(worldId));
        }

        return Register(
            runId,
            worldId,
            agentId: null,
            sessionId: null,
            observer: null,
            requireAudienceIncarnation: false);
    }

    internal Registration Register(AgentRun run)
    {
        return Register(run, requireAudienceIncarnation: false);
    }

    internal Registration Register(
        AgentRun run,
        bool requireAudienceIncarnation)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        var coordinate = requireAudienceIncarnation
            ? GameContextEnvelope.ValidateForRun(run, nameof(run))
            : null;
        return Register(
            RuntimeGuard.RequiredId(run.RunId, nameof(run)),
            RuntimeGuard.RequiredId(run.WorldId, nameof(run)),
            RuntimeGuard.RequiredId(run.AgentId, nameof(run)),
            run.SessionId,
            coordinate?.Observer,
            requireAudienceIncarnation);
    }

    private Registration Register(
        string runId,
        string? worldId,
        string? agentId,
        string? sessionId,
        GameEntityIdentity? observer,
        bool requireAudienceIncarnation)
    {
        var active = new ActiveRunControl(
            _mailboxOptions,
            runId,
            worldId,
            agentId,
            sessionId,
            observer,
            requireAudienceIncarnation,
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
            CancellationToken cancellationToken,
            bool pendingControlAtStart)
        {
            _owner = owner;
            CancellationToken = cancellationToken;
            PendingControlAtStart = pendingControlAtStart;
        }

        public CancellationToken CancellationToken { get; }

        public bool PendingControlAtStart { get; }

        public bool TryAcquireDispatchPermit()
        {
            var owner = Volatile.Read(ref _owner);
            return owner is not null
                   && owner.TryAcquireDispatchPermit();
        }

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
        private bool _stepDispatchBlocked;
        private readonly string _runId;
        private readonly string? _worldId;
        private readonly string? _agentId;
        private readonly string? _sessionId;
        private readonly GameEntityIdentity? _observer;
        private readonly bool _requireAudienceIncarnation;
        private bool _disposed;

        public ActiveRunControl(
            RunControlMailboxOptions mailboxOptions,
            string runId,
            string? worldId,
            string? agentId,
            string? sessionId,
            GameEntityIdentity? observer,
            bool requireAudienceIncarnation,
            BoundedCancellationDispatcher cancellationDispatcher)
        {
            _mailbox = new RunControlMailbox(mailboxOptions);
            _runId = runId;
            _worldId = worldId;
            _agentId = agentId;
            _sessionId = sessionId;
            _observer = observer;
            _requireAudienceIncarnation = requireAudienceIncarnation;
            _cancellationDispatcher = cancellationDispatcher;
        }

        public bool Post(
            RunControlCommand command,
            out string? rejectionReason)
        {
            var accepted = false;
            rejectionReason = null;
            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                if (command.Observation is not null
                    && !ObservationIsVisible(
                        command.Observation,
                        out rejectionReason))
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
                    )
                {
                    _stepDispatchBlocked = true;
                    if (_stepCancellation is null)
                    {
                        _stepCancellation =
                            _stepCancellationReservation!
                                .DispatchAsync(_step);
                    }
                }
            }

            return accepted;
        }

        private bool ObservationIsVisible(
            ObservationEnvelope observation,
            out string? rejectionReason)
        {
            rejectionReason = null;
            if (_worldId is null)
            {
                return true;
            }

            if (_agentId is null)
            {
                var visible = string.Equals(
                    observation.WorldId,
                    _worldId,
                    StringComparison.Ordinal);
                if (!visible)
                {
                    rejectionReason = "observation_world_mismatch";
                }

                return visible;
            }

            try
            {
                ObservationAdmission.EnsureVisibleToRun(
                    observation,
                    _runId,
                    _agentId,
                    _worldId,
                    _sessionId,
                    _observer,
                    _requireAudienceIncarnation);
                return true;
            }
            catch (ObservationAdmissionException exception)
            {
                rejectionReason = exception.ReasonCode;
                return false;
            }
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
                    var pendingControlAtStart =
                        _mailbox.HasStepInterruptingCommand;
                    _stepDispatchBlocked = pendingControlAtStart;
                    if (pendingControlAtStart)
                    {
                        _stepCancellation =
                            cancellationReservation!.DispatchAsync(_step);
                    }
                    return new StepLease(
                        this,
                        _step.Token,
                        pendingControlAtStart);
                }
                catch
                {
                    cancellationReservation!.Dispose();
                    throw;
                }
            }
        }

        public bool TryAcquireDispatchPermit()
        {
            lock (_sync)
            {
                return !_disposed
                       && _step is not null
                       && !_stepDispatchBlocked
                       && !_step.IsCancellationRequested;
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
                _stepDispatchBlocked = false;
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
                _stepDispatchBlocked = true;
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
                try
                {
                    await cancellationTask.ConfigureAwait(false);
                }
                catch
                {
                    // The command remains durably admitted in the mailbox.
                    // A saturated cancellation worker must not fault this
                    // fire-and-forget ownership cleanup.
                }
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
