using System.Collections.Concurrent;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class RuntimeControlPlane
{
    private readonly ConcurrentDictionary<string, ActiveRunControl> _active =
        new(StringComparer.Ordinal);
    private readonly RunControlMailboxOptions _mailboxOptions;

    public RuntimeControlPlane(RunControlMailboxOptions? mailboxOptions = null)
    {
        _mailboxOptions = mailboxOptions ?? new RunControlMailboxOptions();
    }

    public bool TryPost(string runId, RunControlCommand command)
    {
        RuntimeGuard.RequiredId(runId, nameof(runId));
        Validate(command);
        return _active.TryGetValue(runId, out var control)
               && control.Post(command);
    }

    internal Registration Register(string runId)
    {
        runId = RuntimeGuard.RequiredId(runId, nameof(runId));

        var active = new ActiveRunControl(_mailboxOptions);
        if (!_active.TryAdd(runId, active))
        {
            throw new DuplicateRunException(runId);
        }

        return new Registration(this, runId, active);
    }

    private static void Validate(RunControlCommand command)
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

        if (carriesObservation)
        {
            if (command.Observation is null)
            {
                throw new ArgumentException(
                    "Steer and follow-up commands require an observation.",
                    nameof(command));
            }

            ProtocolValidator.EnsureValid(command.Observation);
        }
        else if (command.Observation is not null)
        {
            throw new ArgumentException(
                "Cancel and interrupt commands cannot carry an observation.",
                nameof(command));
        }
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
        private CancellationTokenSource? _step;
        private Task? _stepCancellation;
        private bool _disposed;

        public ActiveRunControl(RunControlMailboxOptions mailboxOptions)
        {
            _mailbox = new RunControlMailbox(mailboxOptions);
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
                    _stepCancellation = CancelDetachedAsync(_step);
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

                _step = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                _stepCancellation = null;
                return new StepLease(this, _step.Token);
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
            lock (_sync)
            {
                step = _step;
                cancellation = _stepCancellation;
                _step = null;
                _stepCancellation = null;
            }

            if (step is null)
            {
                return;
            }

            if (cancellation is null)
            {
                step.Dispose();
            }
            else
            {
                _ = DisposeAfterCancellationAsync(step, cancellation);
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? step;
            Task? cancellation;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                step = _step;
                cancellation = _stepCancellation;
                _step = null;
                _stepCancellation = null;
                _mailbox.Dispose();
            }

            if (step is null)
            {
                return;
            }

            cancellation ??= CancelDetachedAsync(step);
            _ = DisposeAfterCancellationAsync(step, cancellation);
        }

        private static Task CancelDetachedAsync(
            CancellationTokenSource cancellation)
        {
            return Task.Run(
                () =>
                {
                    try
                    {
                        cancellation.Cancel();
                    }
                    catch
                    {
                        // Step cancellation is advisory. A callback cannot
                        // fault the control-plane caller.
                    }
                });
        }

        private static async Task DisposeAfterCancellationAsync(
            CancellationTokenSource cancellation,
            Task cancellationTask)
        {
            try
            {
                await cancellationTask.ConfigureAwait(false);
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }
}
