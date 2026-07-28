using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimeControlPlaneTests
{
    [Fact]
    public async Task SteerCancelsCurrentStepAndCarriesTypedObservation()
    {
        var plane = new RuntimeControlPlane();
        using var registration = plane.Register("run-1");
        using var step = registration.BeginStep(default);
        var observation = Observation();

        Assert.True(
            plane.TryPost(
                "run-1",
                new RunControlCommand
                {
                    CommandId = "control-1",
                    Kind = RunControlKinds.Steer,
                    Observation = observation,
                    CreatedAt = DateTimeOffset.UnixEpoch
                }));

        await WaitUntilAsync(
            () => step.CancellationToken.IsCancellationRequested);
        var command = Assert.Single(registration.Drain());
        Assert.Equal(
            ProtocolJson.Serialize(observation),
            ProtocolJson.Serialize(command.Observation!));
    }

    [Fact]
    public void FollowUpWaitsForBoundaryAndMailboxDoesNotLeakSignals()
    {
        var plane = new RuntimeControlPlane();
        using var registration = plane.Register("run-1");
        using var step = registration.BeginStep(default);

        Assert.True(
            plane.TryPost(
                "run-1",
                new RunControlCommand
                {
                    CommandId = "control-1",
                    Kind = RunControlKinds.FollowUp,
                    Observation = Observation(),
                    CreatedAt = DateTimeOffset.UnixEpoch
                }));

        Assert.False(step.CancellationToken.IsCancellationRequested);
        Assert.Single(registration.Drain());
        Assert.Empty(registration.Drain());
    }

    [Fact]
    public void RejectsInvalidControlPayloads()
    {
        var plane = new RuntimeControlPlane();
        Assert.Throws<ArgumentException>(
            () => plane.TryPost(
                "missing",
                new RunControlCommand
                {
                    CommandId = "control-1",
                    Kind = RunControlKinds.Steer
                }));
        Assert.Throws<ArgumentException>(
            () => plane.TryPost(
                "missing",
                new RunControlCommand
                {
                    CommandId = "control-2",
                    Kind = RunControlKinds.Cancel,
                    Observation = Observation()
                }));
    }

    [Fact]
    public async Task MailboxIsBoundedAndTerminalControlReplacesFollowUp()
    {
        var plane = new RuntimeControlPlane(
            new RunControlMailboxOptions(
                maxCommands: 2,
                maxObservationUtf8Bytes: 8_192,
                maxBufferedObservationUtf8Bytes: 16_384,
                maxRememberedCommandIds: 8));
        using var registration = plane.Register("run-1");
        using var step = registration.BeginStep(default);

        Assert.True(plane.TryPost("run-1", Command("one")));
        Assert.True(plane.TryPost("run-1", Command("two")));
        Assert.False(plane.TryPost("run-1", Command("three")));
        Assert.True(
            plane.TryPost(
                "run-1",
                new RunControlCommand
                {
                    CommandId = "cancel",
                    Kind = RunControlKinds.Cancel,
                    CreatedAt = DateTimeOffset.UnixEpoch
                }));

        await WaitUntilAsync(
            () => step.CancellationToken.IsCancellationRequested);
        var drained = registration.Drain();
        Assert.Equal(2, drained.Count);
        Assert.Equal(RunControlKinds.Cancel, drained[0].Kind);
    }

    [Fact]
    public async Task BlockingStepCallbackCannotBlockControlPostOrDispose()
    {
        var plane = new RuntimeControlPlane();
        var registration = plane.Register("run-1");
        var step = registration.BeginStep(default);
        var callbackInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRegistration = step.CancellationToken.Register(
            () =>
            {
                callbackInvoked.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            });

        try
        {
            Assert.True(
                plane.TryPost(
                    "run-1",
                    new RunControlCommand
                    {
                        CommandId = "control-1",
                        Kind = RunControlKinds.Cancel,
                        CreatedAt = DateTimeOffset.UnixEpoch
                    }));
            await callbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));

            step.Dispose();
            registration.Dispose();
        }
        finally
        {
            release.TrySetResult();
            step.Dispose();
            registration.Dispose();
        }
    }

    [Fact]
    public async Task BlockingStepCancellationKeepsCleanupCapacityBounded()
    {
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        var plane = new RuntimeControlPlane(
            mailboxOptions: null,
            cancellationDispatcher: dispatcher);
        var firstRegistration = plane.Register("run-1");
        var firstStep = firstRegistration.BeginStep(default);
        var callbackInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRegistration =
            firstStep.CancellationToken.Register(
                () =>
                {
                    callbackInvoked.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                });

        try
        {
            Assert.True(
                plane.TryPost(
                    "run-1",
                    new RunControlCommand
                    {
                        CommandId = "cancel-1",
                        Kind = RunControlKinds.Cancel,
                        CreatedAt = DateTimeOffset.UnixEpoch
                    }));
            await callbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstStep.Dispose();
            firstRegistration.Dispose();
            Assert.Equal(1, dispatcher.ActiveReservations);

            using var secondRegistration = plane.Register("run-2");
            Assert.Throws<InvalidOperationException>(
                () => secondRegistration.BeginStep(default));
            Assert.Equal(1, dispatcher.ActiveReservations);
        }
        finally
        {
            release.TrySetResult();
            firstStep.Dispose();
            firstRegistration.Dispose();
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.ActiveReservations == 0,
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void MailboxDeduplicatesIdsAndSnapshotsCallerOwnedObservation()
    {
        var plane = new RuntimeControlPlane();
        using var registration = plane.Register("run-1");
        var observation = Observation();
        var command = new RunControlCommand
        {
            CommandId = "control-1",
            Kind = RunControlKinds.FollowUp,
            Observation = observation,
            CreatedAt = DateTimeOffset.UnixEpoch
        };

        Assert.True(plane.TryPost("run-1", command));
        observation.WorldId = "mutated";
        Assert.False(plane.TryPost("run-1", command));

        var drained = Assert.Single(registration.Drain());
        Assert.Equal("world-1", drained.Observation!.WorldId);
    }

    [Fact]
    public void WorldBoundRegistrationRejectsCrossWorldObservation()
    {
        var plane = new RuntimeControlPlane();
        using var registration = plane.Register("run-1", "world-1");
        var observation = Observation();
        observation.WorldId = "world-2";

        Assert.False(
            plane.TryPost(
                "run-1",
                new RunControlCommand
                {
                    CommandId = "cross-world-control",
                    Kind = RunControlKinds.Steer,
                    Observation = observation,
                    CreatedAt = DateTimeOffset.UnixEpoch
                }));
        Assert.Empty(registration.Drain());
    }

    [Fact]
    public void ObservationTextIsBoundedBeforeMailboxSnapshot()
    {
        var plane = new RuntimeControlPlane(
            new RunControlMailboxOptions(
                maxCommands: 4,
                maxObservationUtf8Bytes: 1_024,
                maxBufferedObservationUtf8Bytes: 4_096,
                maxRememberedCommandIds: 8));
        using var registration = plane.Register("run-1", "world-1");
        var observation = Observation();
        observation.Source = new string('s', 2_048);

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => plane.TryPost(
                "run-1",
                new RunControlCommand
                {
                    CommandId = "oversized-control",
                    Kind = RunControlKinds.Steer,
                    Observation = observation,
                    CreatedAt = DateTimeOffset.UnixEpoch
                }));

        Assert.Equal(
            "control_observation_bytes_exceeded",
            error.LimitCode);
        Assert.Empty(registration.Drain());
    }

    [Fact]
    public void ObservationCollectionsAreBoundedBeforeValidation()
    {
        var plane = new RuntimeControlPlane();
        using var registration = plane.Register("run-1", "world-1");
        var observation = Observation();
        observation.SubjectIds =
            Enumerable.Repeat("duplicate-subject", 2_049).ToList();

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => plane.TryPost(
                "run-1",
                new RunControlCommand
                {
                    CommandId = "oversized-collection-control",
                    Kind = RunControlKinds.Steer,
                    Observation = observation,
                    CreatedAt = DateTimeOffset.UnixEpoch
                }));

        Assert.Equal(
            "control_observation_items_exceeded",
            error.LimitCode);
        Assert.Empty(registration.Drain());
    }

    private static RunControlCommand Command(string id) =>
        new()
        {
            CommandId = id,
            Kind = RunControlKinds.FollowUp,
            Observation = Observation(),
            CreatedAt = DateTimeOffset.UnixEpoch
        };

    private static ObservationEnvelope Observation()
    {
        using var document = JsonDocument.Parse("""{"kind":"input","value":3}""");
        return new ObservationEnvelope
        {
            ObservationId = Guid.NewGuid().ToString("N"),
            WorldId = "world-1",
            Source = "test",
            Kind = "control",
            Payload = document.RootElement.Clone(),
            ObservedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
