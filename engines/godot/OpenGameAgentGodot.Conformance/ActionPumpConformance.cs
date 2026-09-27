using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Godot;
using OpenGameAgent.Protocol;

namespace OpenGameAgent.Godot.Conformance;

public partial class ActionPumpConformance : Node
{
    // These values separate deterministic test scenarios. They are not Runtime defaults or recommendations.
    private const long FixtureTransportBudgetBytes = 65_536;
    private static readonly TimeSpan FixtureWaitTimeout = TimeSpan.FromSeconds(5);

    public override async void _Ready()
    {
        try
        {
            await RunAsync();
            GD.Print("OpenGameAgent Godot action pump conformance passed.");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }

    private async Task RunAsync()
    {
        await RunRecoveryDrainAsync();
        await RunRecoveryDispatchRejectedAsync();
        await RunRecoveryPendingRejectedAsync();
        await RunExistingActionFlowAsync();
        await RunCommitAuthorityMatrixAsync();
        await RunNullCompletionAfterWorldWriteAsync();
        await RunClaimDeadlineScenarioAsync();
        await RunAcceptedUnknownReceiptScenarioAsync();
        await RunAcceptedUnknownReconciliationScenarioAsync();
        await RunCommitEvidenceSnapshotScenarioAsync();
        await RunNoWriteEvidenceSnapshotScenarioAsync();
        await RunInspectionEvidenceSnapshotScenarioAsync();
        await RunCompletionSnapshotDeadlineScenarioAsync();
        await RunNonRetryableClientFailureScenarioAsync();
        await RunRetryExhaustionScenarioAsync(FixtureScenario.RetryableServerFailure);
        await RunRetryExhaustionScenarioAsync(FixtureScenario.AmbiguousTransportFailure);
        await RunTerminalDrainScenarioAsync();
        await RunDeliveryIdentityMismatchScenarioAsync(FixtureScenario.DeliverySessionMismatch);
        await RunDeliveryIdentityMismatchScenarioAsync(FixtureScenario.DeliveryActorMismatch);
        await RunColdReadIdentityMismatchScenarioAsync(FixtureScenario.ColdReadSessionMismatch);
        await RunColdReadIdentityMismatchScenarioAsync(FixtureScenario.ColdReadActorMismatch);
        await RunColdReadIdentityMismatchScenarioAsync(FixtureScenario.ColdReadOperationMismatch);
        await RunRepeatedConflictFenceScenarioAsync();
        await RunChangedConflictFenceScenarioAsync();
    }

    private async Task RunRecoveryDispatchRejectedAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.RecoveryDispatch);
        server.Start();
        var adapter = new TestAuthoritativeAdapter
        {
            Name = "RecoveryDispatchAdapter",
            MainThreadId = System.Environment.CurrentManagedThreadId,
        };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "RecoveryDispatchRuntime",
            executionDurationMilliseconds: 100,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, "recovery dispatch rejection");
            Assert(server.RecoveryAttempts == 1, "the invalid recovery response was retried");
            Assert(server.StreamAttempts == 0, "normal streaming started after an invalid recovery response");
            Assert(adapter.ExecuteCount == 0 && adapter.InspectCount == 0,
                "a recovery dispatch reached the authoritative adapter");
            Assert(failures.Count == 1, "the invalid recovery response did not emit one terminal failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunRecoveryPendingRejectedAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.RecoveryPending);
        server.Start();
        var adapter = new TestAuthoritativeAdapter
        {
            Name = "RecoveryPendingAdapter",
            MainThreadId = System.Environment.CurrentManagedThreadId,
        };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "RecoveryPendingRuntime",
            executionDurationMilliseconds: 100,
            deliveryRetryDelays: []);

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, "pending recovery rejection");
            Assert(server.RecoveryAttempts == 1, "the invalid pending recovery response was retried");
            Assert(adapter.ExecuteCount == 0 && adapter.InspectCount == 0,
                "a pending recovery Action reached the authoritative adapter");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunRecoveryDrainAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.RecoveryDrain);
        server.Start();
        var adapter = new TestAuthoritativeAdapter
        {
            Name = "RecoveryAuthoritativeAdapter",
            MainThreadId = System.Environment.CurrentManagedThreadId,
        };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "RecoveryRuntime",
            executionDurationMilliseconds: 100,
            deliveryRetryDelays: [0]);

        try
        {
            await WaitUntilAsync(() => adapter.StopContext is not null, "recovery drain before streaming");
            Assert(server.RecoveryAttempts == 2, "the action pump did not drain recovery to an empty read");
            Assert(adapter.InspectCount == 1, "the recovered action was not reconciled exactly once");
            Assert(adapter.ExecuteCount == 1, "normal streaming did not start after recovery drained");
            Assert(server.ReconcileBodies.Count == 1, "the recovered action did not settle before streaming");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunExistingActionFlowAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.ExistingFlow);
        server.Start();
        var adapter = new TestAuthoritativeAdapter
        {
            Name = "ExistingAuthoritativeAdapter",
            MainThreadId = System.Environment.CurrentManagedThreadId,
        };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "ExistingRuntime",
            executionDurationMilliseconds: 100,
            deliveryRetryDelays: [0]);
        List<string> runtimeFailures = [];
        runtime.RuntimeFailure += runtimeFailures.Add;

        try
        {
            await WaitUntilAsync(() => adapter.StopContext is not null, "existing action flow");
            Assert(server.ReceiptBodies.Count == 3, "a late background completion was recorded as a receipt");
            Assert(server.ReconcileBodies.Count == 4, "reconcile, timeout, late completion, and conflict were not reconciled");
            Assert(adapter.ExecuteCount == 5, "reconcile delivery invoked Execute or a dispatch was skipped");
            Assert(adapter.InspectCount == 4, "reconcile, timeout, late completion, and conflict were not inspected exactly once");
            Assert(adapter.WorldWrites == 2, "the main-thread write permit did not bound world mutation");
            Assert(adapter.AuthorityRechecks == 2, "expected authority was not rechecked immediately before each write");
            Assert(!adapter.LateCompletionAccepted, "a post-deadline completion reported synchronous acceptance");
            Assert(adapter.ExecutedOnMainThread && adapter.InspectedOnMainThread, "game authority left the Godot main thread");
            Assert(runtimeFailures.Count == 0, $"the action pump emitted an unexpected failure: {string.Join("; ", runtimeFailures)}");

            GodotActionExecutionContext firstContext = adapter.FirstContext
                ?? throw new InvalidOperationException("the first execution context was not delivered");
            GodotActionExecutionContext timedOutContext = adapter.TimedOutContext
                ?? throw new InvalidOperationException("the timed-out execution context was not delivered");
            Assert(
                !firstContext.TryCommit(SuccessfulCompletion),
                "a duplicate callback reused a completed epoch");
            Assert(
                !timedOutContext.TryCommit(SuccessfulCompletion),
                "a callback wrote after its execution deadline closed");
            Assert(
                !timedOutContext.TryCompleteWithoutWorldWrite(UnknownCompletion()),
                "a callback completed after its execution deadline closed");

            AssertFence(server.ReceiptBodies.ElementAt(0), "claim-1", "1");
            AssertFence(server.ReceiptBodies.ElementAt(1), "claim-1", "1");
            AssertFence(server.ReceiptBodies.ElementAt(2), "claim-5", "5");
            AssertFence(server.ReconcileBodies.ElementAt(0), "claim-2", "2");
            AssertFence(server.ReconcileBodies.ElementAt(1), "claim-3", "3");
            AssertFence(server.ReconcileBodies.ElementAt(2), "claim-4", "4");
            AssertFence(server.ReconcileBodies.ElementAt(3), "claim-5", "5");
            Assert(server.ReceiptAttempts == 3, "the explicit 5xx retry and conflict stop conditions were not followed");
            Assert(server.GetAttempts == 2, "the conflicted action read did not follow the explicit finite retry schedule");
        }
        finally
        {
            runtime.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (adapter.StopContext is not null)
            {
                Assert(
                    !adapter.StopContext.TryCompleteWithoutWorldWrite(new GodotActionCompletion { Outcome = ReceiptOutcome.Unknown }),
                    "a callback completed after the node stopped");
            }
            adapter.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task RunCommitAuthorityMatrixAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.CommitAuthority);
        server.Start();
        var adapter = new CommitAuthorityAdapter { Name = "CommitAuthorityAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "CommitAuthorityRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);

        try
        {
            await WaitUntilAsync(() => server.ReceiptAttempts == 3, "background and reentrant commit authority");
            Assert(!adapter.BackgroundCommitAccepted, "a background TryCommit acquired the Godot main-thread write permit");
            Assert(adapter.BackgroundTransactionCalls == 0, "a rejected background TryCommit still invoked its world transaction");
            Assert(adapter.BackgroundWorldWrites == 0, "a rejected background TryCommit mutated the world");
            Task<bool> backgroundCompletion = adapter.BackgroundCompletion
                ?? throw new InvalidOperationException("the background completion fixture did not start");
            Assert(await backgroundCompletion, "an accepted background completion did not report acceptance");
            Assert(adapter.OuterCommitAccepted, "the outer synchronous TryCommit was rejected");
            Assert(!adapter.InnerCommitAccepted, "a synchronous reentrant TryCommit acquired a second write permit");
            Assert(adapter.ReentrantTransactionCalls == 1, "the reentrant transaction executed more than the admitted outer write");
            Assert(adapter.ReentrantWorldWrites == 1, "synchronous reentry produced more than one world write");
            Assert(adapter.RecheckOuterCommitAccepted, "the outer TryCommit lost its permit during authority recheck");
            Assert(!adapter.RecheckNestedCommitAccepted, "authority recheck synchronously reentered the same write permit");
            Assert(adapter.RecheckCalls == 1, "authority recheck recursively invoked itself");
            Assert(adapter.RecheckTransactionCalls == 1, "authority-recheck reentry executed more than the admitted outer transaction");
            Assert(adapter.RecheckWorldWrites == 1, "authority-recheck reentry produced more than one world write");
            Assert(adapter.InspectCount == 0, "successful commit-authority scenarios unexpectedly reconciled");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunClaimDeadlineScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.ClaimDeadline);
        server.Start();
        var adapter = new DeadlineAdapter { Name = "DeadlineAdapter" };
        AddChild(adapter);
        const long hostExecutionMilliseconds = 3_000;
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "DeadlineRuntime",
            executionDurationMilliseconds: hostExecutionMilliseconds,
            deliveryRetryDelays: []);

        try
        {
            await WaitUntilAsync(() => server.ReconcileAttempts == 1, "claim deadline reconciliation");
            Assert(adapter.ExecuteCount == 1, "the unexpired claim did not begin execution");
            Assert(adapter.InspectCount == 1, "claim expiry did not enter authoritative inspection");
            Assert(
                adapter.ElapsedBeforeInspection < TimeSpan.FromMilliseconds(hostExecutionMilliseconds),
                "the host execution duration overrode the earlier claim expiry");
            Assert(adapter.ExecutionWasCancelledBeforeInspection, "claim expiry did not revoke the active execution context");
            Assert(!adapter.LateCompletionAccepted, "a completion was accepted after claim expiry closed its epoch");
            Assert(server.ReceiptAttempts == 0, "an expired claim produced a completion receipt");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunNullCompletionAfterWorldWriteAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.NullCompletionAfterWorldWrite);
        server.Start();
        var adapter = new NullCompletionAdapter { Name = "NullCompletionAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "NullCompletionRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => server.ReconcileAttempts == 1, "post-write null completion reconciliation");
            Assert(adapter.CommitReportedAccepted, "a completed world transaction reported that its permit was rejected");
            Assert(adapter.WorldWrites == 1, "the null-completion fixture did not execute exactly one world transaction");
            Assert(adapter.InspectCount == 1, "an invalid post-write completion did not enter authoritative inspection");
            Assert(server.ReceiptAttempts == 0, "an invalid post-write completion produced a receipt");
            Assert(failures.Count == 1, "an invalid post-write completion did not emit one diagnostic");
            Assert(!runtime.IsActionPumpHalted, "an inspectable invalid post-write completion halted the action pump");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunAcceptedUnknownReceiptScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.AcceptedUnknownReceipt);
        server.Start();
        var adapter = new UnknownReceiptAdapter { Name = "AcceptedUnknownReceiptAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "AcceptedUnknownReceiptRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, "accepted unknown receipt");
            await WaitUntilAsync(() => server.StreamWasClosed, "stream cancellation after an accepted unknown receipt");
            Assert(adapter.ExecuteCount == 1, "the unknown receipt fixture did not execute exactly once");
            Assert(adapter.InspectCount == 0, "an accepted unknown receipt entered local inspection without a new delivery");
            Assert(server.ReceiptAttempts == 1, "the unknown receipt was redelivered after its 2xx acceptance");
            Assert(server.ReconcileAttempts == 0, "the accepted unknown receipt was implicitly reconciled");
            Assert(failures.Count == 1, "the accepted unknown receipt did not halt with one terminal failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunAcceptedUnknownReconciliationScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.AcceptedUnknownReconciliation);
        server.Start();
        var adapter = new UnknownReceiptAdapter { Name = "AcceptedUnknownReconciliationAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "AcceptedUnknownReconciliationRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, "accepted unknown reconciliation");
            await WaitUntilAsync(
                () => server.StreamWasClosed,
                "stream cancellation after an accepted unknown reconciliation");
            Assert(adapter.ExecuteCount == 0, "the reconcile delivery invoked Execute");
            Assert(adapter.InspectCount == 1, "the unknown reconciliation fixture was not inspected exactly once");
            Assert(server.ReceiptAttempts == 0, "the reconcile delivery produced a receipt");
            Assert(server.ReconcileAttempts == 1, "the unknown reconciliation was redelivered after its 2xx acceptance");
            Assert(failures.Count == 1, "the accepted unknown reconciliation did not halt with one terminal failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunCommitEvidenceSnapshotScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.CommitEvidenceSnapshot);
        server.Start();
        var adapter = new CommitEvidenceAdapter(server) { Name = "CommitEvidenceAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "CommitEvidenceRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: [50]);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(
                () => runtime.IsActionPumpHalted || server.ReceiptAttempts == 2,
                "stable commit evidence across receipt retry");
            Assert(!runtime.IsActionPumpHalted, "caller mutation changed an accepted commit into an unknown completion");
            Task mutation = adapter.RetryMutation
                ?? throw new InvalidOperationException("the commit evidence retry mutation did not start");
            await mutation;
            Assert(adapter.WorldWrites == 1, "the commit evidence fixture did not perform exactly one world write");
            Assert(server.ReceiptBodies.Count == 2, "the commit evidence fixture did not exercise one receipt retry");
            AssertReceiptEvidence(server.ReceiptBodies.ElementAt(0), "succeeded", "original", "8");
            AssertReceiptEvidence(server.ReceiptBodies.ElementAt(1), "succeeded", "original", "8");
            Assert(failures.Count == 0, "stable commit evidence emitted an unexpected failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunNoWriteEvidenceSnapshotScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.NoWriteEvidenceSnapshot);
        server.Start();
        var adapter = new NoWriteEvidenceAdapter(server) { Name = "NoWriteEvidenceAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "NoWriteEvidenceRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: [50]);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(
                () => runtime.IsActionPumpHalted || server.ReceiptAttempts == 2,
                "stable no-write evidence across receipt retry");
            Assert(!runtime.IsActionPumpHalted, "caller mutation changed an accepted rejection into an unknown completion");
            Task mutation = adapter.RetryMutation
                ?? throw new InvalidOperationException("the no-write evidence retry mutation did not start");
            await mutation;
            Assert(server.ReceiptBodies.Count == 2, "the no-write evidence fixture did not exercise one receipt retry");
            AssertReceiptEvidence(server.ReceiptBodies.ElementAt(0), "rejected", "original", "8");
            AssertReceiptEvidence(server.ReceiptBodies.ElementAt(1), "rejected", "original", "8");
            Assert(failures.Count == 0, "stable no-write evidence emitted an unexpected failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunInspectionEvidenceSnapshotScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.InspectionEvidenceSnapshot);
        server.Start();
        var adapter = new InspectionEvidenceAdapter(server) { Name = "InspectionEvidenceAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "InspectionEvidenceRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: [50]);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(
                () => runtime.IsActionPumpHalted || server.ReconcileAttempts == 2,
                "stable inspection evidence across reconciliation retry");
            Assert(!runtime.IsActionPumpHalted, "caller mutation changed accepted inspection evidence");
            Task mutation = adapter.RetryMutation
                ?? throw new InvalidOperationException("the inspection evidence retry mutation did not start");
            await mutation;
            Assert(adapter.InspectCount == 1, "the inspection evidence fixture was inspected more than once");
            Assert(server.ReceiptAttempts == 0, "the inspection evidence fixture produced a receipt");
            Assert(server.ReconcileBodies.Count == 2, "the inspection evidence fixture did not exercise one reconciliation retry");
            AssertReconcileEvidence(server.ReconcileBodies.ElementAt(0), "applied", "original", "8");
            AssertReconcileEvidence(server.ReconcileBodies.ElementAt(1), "applied", "original", "8");
            Assert(failures.Count == 0, "stable inspection evidence emitted an unexpected failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunCompletionSnapshotDeadlineScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.CompletionSnapshotDeadline);
        server.Start();
        var adapter = new CompletionSnapshotDeadlineAdapter { Name = "CompletionSnapshotDeadlineAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "CompletionSnapshotDeadlineRuntime",
            executionDurationMilliseconds: 500,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(
                () => server.ReconcileAttempts == 1 || server.ReceiptAttempts == 1,
                "completion snapshot crossing its deadline");
            Task<bool> completion = adapter.BackgroundCompletion
                ?? throw new InvalidOperationException("the deadline snapshot completion did not start");
            Assert(!await completion, "a completion was accepted after its evidence snapshot crossed the deadline");
            Assert(adapter.InspectCount == 1, "the expired snapshot did not enter authoritative inspection");
            Assert(server.ReceiptAttempts == 0, "an expired snapshot produced a receipt");
            Assert(failures.Count == 0, "the expired snapshot emitted an unexpected failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunNonRetryableClientFailureScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.NonRetryableClientFailure);
        server.Start();
        var adapter = new ImmediateSuccessAdapter { Name = "NonRetryableAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "NonRetryableRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: [0, 0]);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, "non-409 4xx terminal failure");
            await WaitUntilAsync(() => server.StreamWasClosed, "action stream cancellation after a non-retryable failure");
            Assert(server.ReceiptAttempts == 1, "an explicit non-409 4xx response was retried");
            Assert(adapter.WorldWrites == 1, "the admitted action did not perform its single authoritative write");
            Assert(failures.Count == 1, "a terminal non-409 4xx failure was not emitted exactly once");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunRetryExhaustionScenarioAsync(FixtureScenario scenario)
    {
        await using var server = new RuntimeFixture(scenario);
        server.Start();
        var adapter = new ImmediateSuccessAdapter { Name = $"{scenario}Adapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            $"{scenario}Runtime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: [0, 0]);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, $"{scenario} retry exhaustion");
            await WaitUntilAsync(() => server.StreamWasClosed, $"{scenario} stream cancellation");
            Assert(server.ReceiptAttempts == 3, $"{scenario} did not consume exactly the host-provided finite retry schedule");
            Assert(adapter.WorldWrites == 1, $"{scenario} replayed the already admitted world write");
            Assert(failures.Count == 1, $"{scenario} did not halt with one terminal failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunTerminalDrainScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.TerminalDrain);
        server.Start();
        var adapter = new ImmediateSuccessAdapter { Name = "TerminalDrainAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "TerminalDrainRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, "normal action-stream terminal state");
            Assert(adapter.ExecuteCount == 1, "the queued delivery was discarded when the action stream ended");
            Assert(adapter.WorldWrites == 1, "the queued delivery did not complete its authoritative write");
            Assert(server.ReceiptAttempts == 1, "the queued delivery was not drained before the terminal state halted the pump");
            Assert(
                failures.Count == 1 && failures[0].Contains("after 1 connection attempts", StringComparison.Ordinal),
                "the normal action-stream terminal state did not produce the bounded terminal failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunDeliveryIdentityMismatchScenarioAsync(FixtureScenario scenario)
    {
        await using var server = new RuntimeFixture(scenario);
        server.Start();
        var adapter = new ImmediateSuccessAdapter { Name = $"{scenario}Adapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            $"{scenario}Runtime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, $"{scenario} authority rejection");
            await WaitUntilAsync(() => server.StreamWasClosed, $"{scenario} stream cancellation");
            Assert(adapter.ExecuteCount == 0, $"{scenario} reached Execute before identity rejection");
            Assert(adapter.InspectCount == 0, $"{scenario} reached Inspect before identity rejection");
            Assert(server.ReceiptAttempts == 0, $"{scenario} produced a receipt before identity rejection");
            Assert(server.ReconcileAttempts == 0, $"{scenario} produced a reconcile before identity rejection");
            Assert(failures.Count == 1, $"{scenario} did not halt with one identity failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunColdReadIdentityMismatchScenarioAsync(FixtureScenario scenario)
    {
        await using var server = new RuntimeFixture(scenario);
        server.Start();
        var adapter = new ImmediateSuccessAdapter { Name = $"{scenario}Adapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            $"{scenario}Runtime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => runtime.IsActionPumpHalted, $"{scenario} cold-read authority rejection");
            await WaitUntilAsync(() => server.StreamWasClosed, $"{scenario} stream cancellation");
            Assert(adapter.ExecuteCount == 1, $"{scenario} did not execute the originally bound delivery once");
            Assert(adapter.WorldWrites == 1, $"{scenario} replayed or skipped the originally bound world write");
            Assert(adapter.InspectCount == 0, $"{scenario} reached Inspect with a mismatched cold-read action");
            Assert(server.ReceiptAttempts == 1, $"{scenario} did not stop receipt delivery at the first 409");
            Assert(server.GetAttempts == 1, $"{scenario} did not stop after the mismatched authoritative read");
            Assert(server.ReconcileAttempts == 0, $"{scenario} reconciled a mismatched authoritative record");
            Assert(failures.Count == 1, $"{scenario} did not halt with one identity failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunRepeatedConflictFenceScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.RepeatedConflictFence);
        server.Start();
        var adapter = new ImmediateSuccessAdapter { Name = "RepeatedConflictFenceAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "RepeatedConflictFenceRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(
                () => runtime.IsActionPumpHalted || server.GetAttempts >= 3,
                "repeated authoritative conflict fence");
            Assert(runtime.IsActionPumpHalted, "an unchanged authoritative conflict fence was inspected indefinitely");
            Assert(adapter.ExecuteCount == 1, "the conflicted action replayed or skipped its original execution");
            Assert(adapter.WorldWrites == 1, "the conflicted action replayed or skipped its admitted world write");
            Assert(adapter.InspectCount == 1, "an unchanged authoritative conflict fence was inspected more than once");
            Assert(server.ReceiptAttempts == 1, "the conflicted receipt was delivered more than once");
            Assert(server.ReconcileAttempts == 1, "the unchanged authoritative conflict fence was reconciled more than once");
            Assert(server.GetAttempts == 2, "the pump did not stop at the first repeated authoritative conflict fence");
            Assert(failures.Count == 1, "the repeated authoritative conflict fence did not halt with one failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private async Task RunChangedConflictFenceScenarioAsync()
    {
        await using var server = new RuntimeFixture(FixtureScenario.ChangedConflictFence);
        server.Start();
        var adapter = new ImmediateSuccessAdapter { Name = "ChangedConflictFenceAdapter" };
        AddChild(adapter);
        OpenGameAgentRuntimeNode runtime = AddRuntime(
            server,
            adapter,
            "ChangedConflictFenceRuntime",
            executionDurationMilliseconds: 1_000,
            deliveryRetryDelays: []);
        List<string> failures = [];
        runtime.RuntimeFailure += failures.Add;

        try
        {
            await WaitUntilAsync(() => server.ReconcileAttempts == 2, "changed authoritative conflict fence");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert(!runtime.IsActionPumpHalted, "a genuinely changed authoritative conflict fence halted the action pump");
            Assert(adapter.ExecuteCount == 1, "a changed conflict fence replayed the original execution");
            Assert(adapter.WorldWrites == 1, "a changed conflict fence replayed the admitted world write");
            Assert(adapter.InspectCount == 2, "each genuinely changed authoritative conflict fence was not inspected once");
            Assert(server.ReceiptAttempts == 1, "a changed conflict fence redelivered the original receipt");
            Assert(server.GetAttempts == 2, "the changed conflict fence did not perform one authoritative read per conflict");
            AssertFence(server.ReconcileBodies.ElementAt(0), "claim-2", "2");
            AssertFence(server.ReconcileBodies.ElementAt(1), "claim-3", "3");
            Assert(failures.Count == 0, "a changed conflict fence emitted an unexpected failure");
        }
        finally
        {
            await RemoveNodesAsync(runtime, adapter);
        }
    }

    private OpenGameAgentRuntimeNode AddRuntime(
        RuntimeFixture server,
        Node adapter,
        string name,
        long executionDurationMilliseconds,
        IReadOnlyList<int> deliveryRetryDelays)
    {
        var delays = new global::Godot.Collections.Array<int>();
        foreach (int delay in deliveryRetryDelays) delays.Add(delay);
        var runtime = new OpenGameAgentRuntimeNode
        {
            Name = name,
            BaseUrl = server.BaseAddress,
            SessionId = "session",
            ActorId = "actor",
            AuthoritativeAdapterPath = adapter.GetPath(),
            MaxRequestBytes = FixtureTransportBudgetBytes,
            MaxHttpResponseBytes = FixtureTransportBudgetBytes,
            MaxAttachmentResponseBytes = FixtureTransportBudgetBytes,
            MaxSseEventBytes = FixtureTransportBudgetBytes,
            MaxQueuedActions = 1,
            ExecutionDurationMilliseconds = executionDurationMilliseconds,
            DeliveryRetryDelayMilliseconds = delays,
            RetryDefaultDelayMilliseconds = 1,
            RetryMaximumDelayMilliseconds = 1,
            MaximumConnectionAttempts = 1,
        };
        AddChild(runtime);
        return runtime;
    }

    private async Task RemoveNodesAsync(params Node[] nodes)
    {
        foreach (Node node in nodes) node.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed >= FixtureWaitTimeout)
            {
                throw new TimeoutException($"Godot action pump fixture timed out while waiting for {scenario}.");
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static GodotActionCompletion SuccessfulCompletion() => new()
    {
        Outcome = ReceiptOutcome.Succeeded,
        Result = new { applied = true },
        AuthorityAfter = new GodotAuthorityVersion
        {
            TimelineId = "world",
            Generation = "2",
            Revision = "8",
        },
    };

    private static GodotActionCompletion UnknownCompletion() => new()
    {
        Outcome = ReceiptOutcome.Unknown,
    };

    private static GodotActionCompletion RejectedCompletion() => new()
    {
        Outcome = ReceiptOutcome.Rejected,
    };

    private static void AssertFence(string body, string claimToken, string attempt)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string actualClaimToken = root.GetProperty("claimToken").GetString() ?? string.Empty;
        string actualAttempt = root.GetProperty("attempt").GetString() ?? string.Empty;
        Assert(actualClaimToken == claimToken, $"claim token fence changed: expected {claimToken}, received {actualClaimToken}");
        Assert(actualAttempt == attempt, $"attempt fence changed: expected {attempt}, received {actualAttempt}");
    }

    private static void AssertReceiptEvidence(
        string body,
        string outcome,
        string marker,
        string authorityRevision)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert(root.GetProperty("outcome").GetString() == outcome, "caller mutation changed the receipt outcome");
        Assert(
            root.GetProperty("result").GetProperty("nested").GetProperty("marker").GetString() == marker,
            "caller mutation changed nested receipt result evidence");
        Assert(
            root.GetProperty("modelResult").GetProperty("nested").GetProperty("marker").GetString() == marker,
            "caller mutation changed nested receipt model evidence");
        Assert(
            root.GetProperty("authorityAfter").GetProperty("revision").GetString() == authorityRevision,
            "caller mutation changed receipt authority evidence");
    }

    private static void AssertReconcileEvidence(
        string body,
        string resolution,
        string marker,
        string authorityRevision)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert(root.GetProperty("resolution").GetString() == resolution, "caller mutation changed the reconciliation resolution");
        Assert(
            root.GetProperty("result").GetProperty("nested").GetProperty("marker").GetString() == marker,
            "caller mutation changed nested reconciliation result evidence");
        Assert(
            root.GetProperty("modelResult").GetProperty("nested").GetProperty("marker").GetString() == marker,
            "caller mutation changed nested reconciliation model evidence");
        Assert(
            root.GetProperty("authorityAfter").GetProperty("revision").GetString() == authorityRevision,
            "caller mutation changed reconciliation authority evidence");
    }

    private static Dictionary<string, object?> Evidence(string marker) => new()
    {
        ["nested"] = new Dictionary<string, object?> { ["marker"] = marker },
    };

    private static void MutateNestedEvidence(
        Dictionary<string, object?> result,
        Dictionary<string, object?> modelResult,
        string marker)
    {
        ((Dictionary<string, object?>)result["nested"]!)["marker"] = marker;
        ((Dictionary<string, object?>)modelResult["nested"]!)["marker"] = marker;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed partial class TestAuthoritativeAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        public int MainThreadId { get; init; }

        public int ExecuteCount { get; private set; }

        public int InspectCount { get; private set; }

        public int WorldWrites { get; private set; }

        public int AuthorityRechecks { get; private set; }

        public bool ExecutedOnMainThread { get; private set; } = true;

        public bool InspectedOnMainThread { get; private set; } = true;

        public bool LateCompletionAccepted { get; private set; }

        public GodotActionExecutionContext? FirstContext { get; private set; }

        public GodotActionExecutionContext? TimedOutContext { get; private set; }

        public GodotActionExecutionContext? StopContext { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            InspectCount++;
            InspectedOnMainThread &= System.Environment.CurrentManagedThreadId == MainThreadId;
            return new GodotActionInspection { Resolution = Resolution.NotApplied };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            ExecuteCount++;
            ExecutedOnMainThread &= System.Environment.CurrentManagedThreadId == MainThreadId;
            switch (delivery.OperationKey)
            {
                case "immediate":
                    FirstContext = context;
                    Assert(
                        !context.TryCompleteWithoutWorldWrite(SuccessfulCompletion()),
                        "a successful completion bypassed the authoritative world-write permit");
                    Assert(context.TryCommit(() =>
                    {
                        WorldWrites++;
                        return SuccessfulCompletion();
                    }), "an admitted main-thread action could not obtain its permit");
                    break;
                case "timeout":
                    TimedOutContext = context;
                    break;
                case "late":
                    using (var queued = new ManualResetEventSlim())
                    {
                        _ = Task.Run(() =>
                        {
                            Thread.Sleep(150);
                            LateCompletionAccepted = context.TryCompleteWithoutWorldWrite(UnknownCompletion());
                            queued.Set();
                        });
                        Assert(queued.Wait(TimeSpan.FromSeconds(1)), "the late completion fixture did not run");
                    }
                    break;
                case "conflict":
                    Assert(context.TryCommit(() =>
                    {
                        WorldWrites++;
                        return SuccessfulCompletion();
                    }), "a conflict fixture action could not obtain its permit");
                    break;
                case "stop":
                    StopContext = context;
                    break;
            }
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            AuthorityRechecks++;
            return true;
        }
    }

    private sealed partial class CommitAuthorityAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        public bool BackgroundCommitAccepted { get; private set; }

        public Task<bool>? BackgroundCompletion { get; private set; }

        public int BackgroundTransactionCalls { get; private set; }

        public int BackgroundWorldWrites { get; private set; }

        public bool OuterCommitAccepted { get; private set; }

        public bool InnerCommitAccepted { get; private set; }

        public int ReentrantTransactionCalls { get; private set; }

        public int ReentrantWorldWrites { get; private set; }

        public bool RecheckOuterCommitAccepted { get; private set; }

        public bool RecheckNestedCommitAccepted { get; private set; }

        public int RecheckCalls { get; private set; }

        public int RecheckTransactionCalls { get; private set; }

        public int RecheckWorldWrites { get; private set; }

        public int InspectCount { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            InspectCount++;
            return new GodotActionInspection { Resolution = Resolution.NotApplied };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            if (delivery.OperationKey == "background")
            {
                BackgroundCommitAccepted = Task.Run(() => context.TryCommit(() =>
                {
                    BackgroundTransactionCalls++;
                    BackgroundWorldWrites++;
                    return SuccessfulCompletion();
                })).GetAwaiter().GetResult();
                BackgroundCompletion = Task.Run(() => context.TryCompleteWithoutWorldWrite(RejectedCompletion()));
                return;
            }

            if (delivery.OperationKey == "authority-reentry")
            {
                recheckContext = context;
                RecheckOuterCommitAccepted = context.TryCommit(() =>
                {
                    RecheckTransactionCalls++;
                    RecheckWorldWrites++;
                    return SuccessfulCompletion();
                });
                return;
            }

            OuterCommitAccepted = context.TryCommit(() =>
            {
                ReentrantTransactionCalls++;
                ReentrantWorldWrites++;
                InnerCommitAccepted = context.TryCommit(() =>
                {
                    ReentrantTransactionCalls++;
                    ReentrantWorldWrites++;
                    return SuccessfulCompletion();
                });
                return SuccessfulCompletion();
            });
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            if (delivery.OperationKey == "authority-reentry")
            {
                RecheckCalls++;
                if (RecheckCalls == 1)
                {
                    GodotActionExecutionContext context = recheckContext
                        ?? throw new InvalidOperationException("the authority-reentry context was not captured");
                    RecheckNestedCommitAccepted = context.TryCommit(() =>
                    {
                        RecheckTransactionCalls++;
                        RecheckWorldWrites++;
                        return SuccessfulCompletion();
                    });
                }
            }
            return true;
        }

        private GodotActionExecutionContext? recheckContext;
    }

    private sealed partial class NullCompletionAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        public bool CommitReportedAccepted { get; private set; }

        public int InspectCount { get; private set; }

        public int WorldWrites { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            InspectCount++;
            return new GodotActionInspection { Resolution = Resolution.Applied };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            _ = delivery;
            CommitReportedAccepted = context.TryCommit(() =>
            {
                WorldWrites++;
                return null!;
            });
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            return true;
        }
    }

    private sealed partial class DeadlineAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        private Stopwatch? execution;
        private GodotActionExecutionContext? context;

        public int ExecuteCount { get; private set; }

        public int InspectCount { get; private set; }

        public TimeSpan ElapsedBeforeInspection { get; private set; }

        public bool ExecutionWasCancelledBeforeInspection { get; private set; }

        public bool LateCompletionAccepted { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            InspectCount++;
            ElapsedBeforeInspection = execution?.Elapsed ?? TimeSpan.MaxValue;
            ExecutionWasCancelledBeforeInspection = context?.CancellationToken.IsCancellationRequested == true;
            LateCompletionAccepted = context?.TryCompleteWithoutWorldWrite(UnknownCompletion()) == true;
            return new GodotActionInspection { Resolution = Resolution.NotApplied };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext executionContext)
        {
            _ = delivery;
            ExecuteCount++;
            context = executionContext;
            execution = Stopwatch.StartNew();
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            return true;
        }
    }

    private sealed partial class ImmediateSuccessAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        public int ExecuteCount { get; private set; }

        public int InspectCount { get; private set; }

        public int WorldWrites { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            InspectCount++;
            return new GodotActionInspection { Resolution = Resolution.NotApplied };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            _ = delivery;
            ExecuteCount++;
            Assert(context.TryCommit(() =>
            {
                WorldWrites++;
                return SuccessfulCompletion();
            }), "the immediate-success fixture could not obtain its main-thread write permit");
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            return true;
        }
    }

    private sealed partial class UnknownReceiptAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        public int ExecuteCount { get; private set; }

        public int InspectCount { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            InspectCount++;
            return new GodotActionInspection { Resolution = Resolution.Unknown };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            _ = delivery;
            ExecuteCount++;
            Assert(
                context.TryCompleteWithoutWorldWrite(UnknownCompletion()),
                "the unknown receipt fixture could not submit its completion");
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            return true;
        }
    }

    private sealed partial class CommitEvidenceAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        private readonly RuntimeFixture server;

        public CommitEvidenceAdapter(RuntimeFixture server)
        {
            this.server = server;
        }

        public Task? RetryMutation { get; private set; }

        public int WorldWrites { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            return new GodotActionInspection { Resolution = Resolution.NotApplied };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            _ = delivery;
            var result = Evidence("original");
            var modelResult = Evidence("original");
            var authority = new GodotAuthorityVersion
            {
                TimelineId = "world",
                Generation = "2",
                Revision = "8",
            };
            var completion = new GodotActionCompletion
            {
                Outcome = ReceiptOutcome.Succeeded,
                Result = result,
                ModelResult = modelResult,
                AuthorityAfter = authority,
            };
            Assert(context.TryCommit(() =>
            {
                WorldWrites++;
                return completion;
            }), "the commit evidence fixture could not obtain its write permit");

            MutateNestedEvidence(result, modelResult, "after-acceptance");
            RetryMutation = Task.Run(() =>
            {
                Assert(
                    SpinWait.SpinUntil(() => server.ReceiptAttempts >= 1, TimeSpan.FromSeconds(1)),
                    "the commit evidence fixture did not observe its first receipt attempt");
                MutateNestedEvidence(result, modelResult, "between-retries");
            });
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            return true;
        }

    }

    private sealed partial class NoWriteEvidenceAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        private readonly RuntimeFixture server;

        public NoWriteEvidenceAdapter(RuntimeFixture server)
        {
            this.server = server;
        }

        public Task? RetryMutation { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            return new GodotActionInspection { Resolution = Resolution.NotApplied };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            _ = delivery;
            var result = Evidence("original");
            var modelResult = Evidence("original");
            var authority = new GodotAuthorityVersion
            {
                TimelineId = "world",
                Generation = "2",
                Revision = "8",
            };
            var completion = new GodotActionCompletion
            {
                Outcome = ReceiptOutcome.Rejected,
                Result = result,
                ModelResult = modelResult,
                AuthorityAfter = authority,
            };
            Assert(
                context.TryCompleteWithoutWorldWrite(completion),
                "the no-write evidence fixture could not submit its completion");
            MutateNestedEvidence(result, modelResult, "after-acceptance");
            RetryMutation = Task.Run(() =>
            {
                Assert(
                    SpinWait.SpinUntil(() => server.ReceiptAttempts >= 1, TimeSpan.FromSeconds(1)),
                    "the no-write evidence fixture did not observe its first receipt attempt");
                MutateNestedEvidence(result, modelResult, "between-retries");
            });
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            return true;
        }
    }

    private sealed partial class InspectionEvidenceAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        private readonly RuntimeFixture server;

        public InspectionEvidenceAdapter(RuntimeFixture server)
        {
            this.server = server;
        }

        public int InspectCount { get; private set; }

        public Task? RetryMutation { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            InspectCount++;
            var result = Evidence("original");
            var modelResult = Evidence("original");
            var inspection = new GodotActionInspection
            {
                Resolution = Resolution.Applied,
                Result = result,
                ModelResult = modelResult,
                AuthorityAfter = new GodotAuthorityVersion
                {
                    TimelineId = "world",
                    Generation = "2",
                    Revision = "8",
                },
            };
            RetryMutation = Task.Run(() =>
            {
                Assert(
                    SpinWait.SpinUntil(() => server.ReconcileAttempts >= 1, TimeSpan.FromSeconds(1)),
                    "the inspection evidence fixture did not observe its first reconciliation attempt");
                MutateNestedEvidence(result, modelResult, "between-retries");
            });
            return inspection;
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            _ = delivery;
            _ = context;
            throw new InvalidOperationException("the inspection evidence fixture must not execute a reconcile delivery");
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            return true;
        }
    }

    private sealed partial class CompletionSnapshotDeadlineAdapter : Node, IGodotAuthoritativeActionAdapter
    {
        private readonly ManualResetEventSlim releaseSnapshot = new();
        private readonly ManualResetEventSlim snapshotStarted = new();

        public Task<bool>? BackgroundCompletion { get; private set; }

        public int InspectCount { get; private set; }

        public GodotActionInspection Inspect(GodotActionDelivery delivery)
        {
            _ = delivery;
            InspectCount++;
            return new GodotActionInspection { Resolution = Resolution.NotApplied };
        }

        public void Execute(GodotActionDelivery delivery, GodotActionExecutionContext context)
        {
            _ = delivery;
            var completion = new GodotActionCompletion
            {
                Outcome = ReceiptOutcome.Rejected,
                Result = new BlockingEvidence(snapshotStarted, releaseSnapshot),
            };
            BackgroundCompletion = Task.Run(() => context.TryCompleteWithoutWorldWrite(completion));
            Assert(snapshotStarted.Wait(FixtureWaitTimeout), "the background completion did not begin its evidence snapshot");
            Thread.Sleep(600);
            releaseSnapshot.Set();
            _ = BackgroundCompletion.GetAwaiter().GetResult();
        }

        public bool RecheckExpectedAuthority(GodotActionDelivery delivery)
        {
            _ = delivery;
            return true;
        }
    }

    private sealed class BlockingEvidence
    {
        private readonly ManualResetEventSlim release;
        private readonly ManualResetEventSlim started;

        public BlockingEvidence(ManualResetEventSlim started, ManualResetEventSlim release)
        {
            this.started = started;
            this.release = release;
        }

        public string Marker
        {
            get
            {
                started.Set();
                if (!release.Wait(FixtureWaitTimeout)) throw new TimeoutException("the evidence snapshot was not released");
                return "original";
            }
        }
    }

    private enum FixtureScenario
    {
        RecoveryDrain,
        RecoveryDispatch,
        RecoveryPending,
        ExistingFlow,
        CommitAuthority,
        NullCompletionAfterWorldWrite,
        ClaimDeadline,
        NonRetryableClientFailure,
        RetryableServerFailure,
        AmbiguousTransportFailure,
        TerminalDrain,
        DeliverySessionMismatch,
        DeliveryActorMismatch,
        ColdReadSessionMismatch,
        ColdReadActorMismatch,
        ColdReadOperationMismatch,
        RepeatedConflictFence,
        ChangedConflictFence,
        AcceptedUnknownReceipt,
        AcceptedUnknownReconciliation,
        CommitEvidenceSnapshot,
        NoWriteEvidenceSnapshot,
        InspectionEvidenceSnapshot,
        CompletionSnapshotDeadline,
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly ConcurrentBag<Task> connections = [];
        private readonly CancellationTokenSource lifetime = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly FixtureScenario scenario;
        private readonly TaskCompletionSource<bool> streamClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int getAttempts;
        private int recoveryAttempts;
        private int receiptAttempts;
        private int reconcileAttempts;
        private int streamAttempts;
        private Task? serverTask;

        public RuntimeFixture(FixtureScenario scenario)
        {
            this.scenario = scenario;
        }

        public ConcurrentQueue<string> ReceiptBodies { get; } = new();

        public ConcurrentQueue<string> ReconcileBodies { get; } = new();

        public int ReceiptAttempts => Volatile.Read(ref receiptAttempts);

        public int ReconcileAttempts => Volatile.Read(ref reconcileAttempts);

        public int GetAttempts => Volatile.Read(ref getAttempts);

        public int RecoveryAttempts => Volatile.Read(ref recoveryAttempts);

        public int StreamAttempts => Volatile.Read(ref streamAttempts);

        public bool StreamWasClosed => streamClosed.Task.IsCompletedSuccessfully;

        public string BaseAddress
        {
            get
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                return $"http://127.0.0.1:{port}";
            }
        }

        public void Start()
        {
            listener.Start();
            serverTask = RunAsync(lifetime.Token);
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            if (serverTask is not null)
            {
                try
                {
                    await serverTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (SocketException) when (lifetime.IsCancellationRequested)
                {
                }
            }

            try
            {
                await Task.WhenAll(connections.ToArray());
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (SocketException) when (lifetime.IsCancellationRequested)
            {
            }
            lifetime.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                connections.Add(HandleClientAsync(client, cancellationToken));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (NetworkStream stream = client.GetStream())
            {
                RuntimeRequest request = await ReadRequestAsync(stream, cancellationToken);
                switch (request.Path)
                {
                    case "/runtime/v1/actions/recovery":
                        int recoveryAttempt = Interlocked.Increment(ref recoveryAttempts);
                        await RespondAsync(
                            stream,
                            HttpStatusCode.OK,
                            scenario switch
                            {
                                FixtureScenario.RecoveryDrain when recoveryAttempt == 1 => RecoveryResponse(1, "recovery"),
                                FixtureScenario.RecoveryDispatch => RecoveryResponse(1, "invalid-recovery", "dispatch"),
                                FixtureScenario.RecoveryPending => RecoveryResponse(
                                    1,
                                    "invalid-pending-recovery",
                                    status: "pending"),
                                _ => "{\"delivery\":null}",
                            },
                            "application/json",
                            cancellationToken);
                        break;
                    case "/runtime/v1/actions/stream":
                        Interlocked.Increment(ref streamAttempts);
                        await RespondActionStreamAsync(stream, cancellationToken);
                        break;
                    case "/runtime/v1/actions/receipt":
                        ReceiptBodies.Enqueue(request.Body);
                        int receiptAttempt = Interlocked.Increment(ref receiptAttempts);
                        await RespondToReceiptAsync(stream, receiptAttempt, cancellationToken);
                        break;
                    case "/runtime/v1/actions/reconcile":
                        ReconcileBodies.Enqueue(request.Body);
                        int reconcileAttempt = Interlocked.Increment(ref reconcileAttempts);
                        if (scenario == FixtureScenario.InspectionEvidenceSnapshot)
                        {
                            await RespondAsync(
                                stream,
                                reconcileAttempt == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK,
                                reconcileAttempt == 1
                                    ? ErrorBody("internal_error", "retry")
                                    : ActionResponse(1, "inspection-evidence", status: "succeeded"),
                                "application/json",
                                cancellationToken);
                            break;
                        }
                        if (scenario == FixtureScenario.RepeatedConflictFence
                            || (scenario == FixtureScenario.ChangedConflictFence && reconcileAttempt == 1))
                        {
                            await RespondAsync(
                                stream,
                                HttpStatusCode.Conflict,
                                ErrorBody("action_conflict", "inspect"),
                                "application/json",
                                cancellationToken);
                            break;
                        }
                        if (scenario == FixtureScenario.AcceptedUnknownReconciliation)
                        {
                            await RespondAsync(
                                stream,
                                HttpStatusCode.OK,
                                ActionResponse(1, "reconcile", status: "uncertain"),
                                "application/json",
                                cancellationToken);
                            break;
                        }
                        int actionAttempt = scenario == FixtureScenario.ExistingFlow
                            ? reconcileAttempt + 1
                            : 1;
                        string operationKey = scenario == FixtureScenario.ExistingFlow
                            ? actionAttempt switch
                            {
                                2 => "reconcile",
                                3 => "timeout",
                                4 => "late",
                                _ => "conflict",
                            }
                            : "deadline";
                        await RespondAsync(
                            stream,
                            HttpStatusCode.OK,
                            ActionResponse(actionAttempt, operationKey, status: "pending"),
                            "application/json",
                            cancellationToken);
                        break;
                    case "/runtime/v1/actions/get":
                        int getAttempt = Interlocked.Increment(ref getAttempts);
                        if (scenario == FixtureScenario.ExistingFlow && getAttempt == 1)
                        {
                            await RespondAsync(
                                stream,
                                HttpStatusCode.InternalServerError,
                                ErrorBody("internal_error", "retry"),
                                "application/json",
                                cancellationToken);
                        }
                        else
                        {
                            string sessionId = scenario == FixtureScenario.ColdReadSessionMismatch ? "other-session" : "session";
                            string actorId = scenario == FixtureScenario.ColdReadActorMismatch ? "other-actor" : "actor";
                            string? operationId = scenario == FixtureScenario.ColdReadOperationMismatch
                                ? $"act_{new string('c', 64)}"
                                : null;
                            int readActionAttempt = scenario switch
                            {
                                FixtureScenario.RepeatedConflictFence => 2,
                                FixtureScenario.ChangedConflictFence => getAttempt + 1,
                                FixtureScenario.ExistingFlow => 5,
                                _ => 1,
                            };
                            if (scenario is FixtureScenario.RepeatedConflictFence or FixtureScenario.ChangedConflictFence)
                            {
                                operationId = $"act_{"1".PadLeft(64, '0')}";
                            }
                            await RespondAsync(
                                stream,
                                HttpStatusCode.OK,
                                ActionResponse(
                                    readActionAttempt,
                                    "conflict",
                                    sessionId,
                                    actorId,
                                    operationId),
                                "application/json",
                                cancellationToken);
                        }
                        break;
                    default:
                        await RespondAsync(stream, HttpStatusCode.NotFound, "{}", "application/json", cancellationToken);
                        break;
                }
            }
        }

        private async Task RespondActionStreamAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            string body = ActionStream();
            if (scenario is FixtureScenario.ExistingFlow or FixtureScenario.TerminalDrain)
            {
                await RespondAsync(stream, HttpStatusCode.OK, body, "text/event-stream", cancellationToken);
                return;
            }

            string headers =
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: text/event-stream\r\n"
                + "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await ObservePeerCloseAsync(stream, cancellationToken);
        }

        private async Task ObservePeerCloseAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1];
            try
            {
                while (await stream.ReadAsync(buffer, cancellationToken) != 0)
                {
                }
                if (!cancellationToken.IsCancellationRequested) streamClosed.TrySetResult(true);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                streamClosed.TrySetResult(true);
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                streamClosed.TrySetResult(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task RespondToReceiptAsync(
            NetworkStream stream,
            int attempt,
            CancellationToken cancellationToken)
        {
            if (scenario == FixtureScenario.AmbiguousTransportFailure) return;
            if (scenario is FixtureScenario.CommitEvidenceSnapshot or FixtureScenario.NoWriteEvidenceSnapshot)
            {
                if (attempt == 1)
                {
                    await RespondAsync(
                        stream,
                        HttpStatusCode.InternalServerError,
                        ErrorBody("internal_error", "retry"),
                        "application/json",
                        cancellationToken);
                }
                else
                {
                    await RespondAsync(
                        stream,
                        HttpStatusCode.OK,
                        ActionResponse(
                            1,
                            scenario == FixtureScenario.CommitEvidenceSnapshot ? "commit-evidence" : "no-write-evidence",
                            status: scenario == FixtureScenario.CommitEvidenceSnapshot ? "succeeded" : "failed"),
                        "application/json",
                        cancellationToken);
                }
                return;
            }
            if (scenario == FixtureScenario.NonRetryableClientFailure)
            {
                await RespondAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    ErrorBody("invalid_request", "halt"),
                    "application/json",
                    cancellationToken);
                return;
            }
            if (scenario == FixtureScenario.RetryableServerFailure
                || (scenario == FixtureScenario.ExistingFlow && attempt == 1))
            {
                await RespondAsync(
                    stream,
                    HttpStatusCode.InternalServerError,
                    ErrorBody("internal_error", "retry"),
                    "application/json",
                    cancellationToken);
                return;
            }
            if (scenario == FixtureScenario.ExistingFlow && attempt == 3)
            {
                await RespondAsync(
                    stream,
                    HttpStatusCode.Conflict,
                    ErrorBody("action_conflict", "inspect"),
                    "application/json",
                    cancellationToken);
                return;
            }
            if (scenario is FixtureScenario.ColdReadSessionMismatch
                or FixtureScenario.ColdReadActorMismatch
                or FixtureScenario.ColdReadOperationMismatch
                or FixtureScenario.RepeatedConflictFence
                or FixtureScenario.ChangedConflictFence)
            {
                await RespondAsync(
                    stream,
                    HttpStatusCode.Conflict,
                    ErrorBody("action_conflict", "inspect"),
                    "application/json",
                    cancellationToken);
                return;
            }

            if (scenario == FixtureScenario.AcceptedUnknownReceipt)
            {
                await RespondAsync(
                    stream,
                    HttpStatusCode.OK,
                    ActionResponse(1, "immediate", status: "uncertain"),
                    "application/json",
                    cancellationToken);
                return;
            }

            int actionAttempt = scenario == FixtureScenario.CommitAuthority ? attempt : 1;
            string operationKey = scenario == FixtureScenario.CommitAuthority
                ? attempt switch
                {
                    2 => "reentrant",
                    3 => "authority-reentry",
                    _ => "background",
                }
                : "immediate";
            await RespondAsync(
                stream,
                HttpStatusCode.OK,
                ActionResponse(
                    actionAttempt,
                    operationKey,
                    status: scenario == FixtureScenario.CommitAuthority && attempt == 1 ? "failed" : "succeeded"),
                "application/json",
                cancellationToken);
        }

        private string ActionStream()
        {
            return scenario switch
            {
                FixtureScenario.ExistingFlow => string.Concat(
                    DeliveryFrame(1, "immediate"),
                    DeliveryFrame(2, "reconcile", RuntimeActionDeliveryKind.Reconcile),
                    DeliveryFrame(3, "timeout"),
                    DeliveryFrame(4, "late"),
                    DeliveryFrame(5, "conflict"),
                    DeliveryFrame(6, "stop")),
                FixtureScenario.RecoveryDrain => DeliveryFrame(2, "stop"),
                FixtureScenario.CommitAuthority => string.Concat(
                    DeliveryFrame(1, "background"),
                    DeliveryFrame(2, "reentrant"),
                    DeliveryFrame(3, "authority-reentry")),
                FixtureScenario.NullCompletionAfterWorldWrite => DeliveryFrame(1, "null-completion"),
                FixtureScenario.ClaimDeadline => DeliveryFrame(
                    1,
                    "deadline",
                    claimLifetime: TimeSpan.FromMilliseconds(400)),
                FixtureScenario.AcceptedUnknownReconciliation => DeliveryFrame(
                    1,
                    "reconcile",
                    RuntimeActionDeliveryKind.Reconcile),
                FixtureScenario.CommitEvidenceSnapshot => DeliveryFrame(1, "commit-evidence"),
                FixtureScenario.NoWriteEvidenceSnapshot => DeliveryFrame(1, "no-write-evidence"),
                FixtureScenario.InspectionEvidenceSnapshot => DeliveryFrame(
                    1,
                    "inspection-evidence",
                    RuntimeActionDeliveryKind.Reconcile),
                FixtureScenario.CompletionSnapshotDeadline => DeliveryFrame(1, "snapshot-deadline"),
                FixtureScenario.DeliverySessionMismatch => DeliveryFrame(
                    1,
                    "immediate",
                    sessionId: "other-session"),
                FixtureScenario.DeliveryActorMismatch => DeliveryFrame(
                    1,
                    "immediate",
                    actorId: "other-actor"),
                _ => DeliveryFrame(1, "immediate"),
            };
        }

        private static string DeliveryFrame(
            int attempt,
            string operationKey,
            RuntimeActionDeliveryKind kind = RuntimeActionDeliveryKind.Dispatch,
            TimeSpan? claimLifetime = null,
            string sessionId = "session",
            string actorId = "actor")
        {
            string kindText = kind == RuntimeActionDeliveryKind.Dispatch ? "dispatch" : "reconcile";
            return $"data: {{\"type\":\"delivery\",\"delivery\":{{\"kind\":\"{kindText}\",\"action\":{ActionJson(attempt, operationKey, claimLifetime, sessionId, actorId)}}}}}\n\n";
        }

        private static string ActionResponse(
            int attempt,
            string operationKey,
            string sessionId = "session",
            string actorId = "actor",
            string? operationId = null,
            string status = "claimed") =>
            $"{{\"action\":{ActionJson(attempt, operationKey, null, sessionId, actorId, operationId, status)}}}";

        private static string RecoveryResponse(
            int attempt,
            string operationKey,
            string kind = "reconcile",
            string status = "claimed")
        {
            string action = ActionJson(attempt, operationKey, status: status);
            if (status == "pending")
            {
                string claimed = ActionJson(attempt, operationKey);
                using JsonDocument claimedDocument = JsonDocument.Parse(claimed);
                using JsonDocument pendingDocument = JsonDocument.Parse(action);
                var fields = pendingDocument.RootElement.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.Clone());
                fields["claim"] = claimedDocument.RootElement.GetProperty("claim").Clone();
                action = JsonSerializer.Serialize(fields);
            }
            return $"{{\"delivery\":{{\"kind\":\"{kind}\",\"action\":{action}}}}}";
        }

        private static string ActionJson(
            int attempt,
            string operationKey,
            TimeSpan? claimLifetime = null,
            string sessionId = "session",
            string actorId = "actor",
            string? operationId = null,
            string status = "claimed")
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string timestamp = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string expiresAt = now.Add(claimLifetime ?? TimeSpan.FromMinutes(10)).ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);
            operationId ??= $"act_{attempt.ToString(CultureInfo.InvariantCulture).PadLeft(64, '0')}";
            string attemptText = attempt.ToString(CultureInfo.InvariantCulture);
            var action = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["actorId"] = actorId,
                ["operationId"] = operationId,
                ["intentFingerprint"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ["payloadFingerprint"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                ["coordinate"] = new { sessionId, actorId, runId = "run", turn = "1" },
                ["actionName"] = "world.write",
                ["operationKey"] = operationKey,
                ["payload"] = new { value = attempt },
                ["expectedAuthority"] = new { timelineId = "world", generation = "2", revision = "7" },
                ["conflictKeys"] = new[] { "world:tile:1" },
                ["replayPolicy"] = "recoverable",
                ["status"] = status,
                ["attempt"] = attemptText,
                ["createdAt"] = timestamp,
                ["updatedAt"] = timestamp,
            };
            if (status is "claimed" or "uncertain")
            {
                action["claim"] = new
                {
                    claimantId = "game",
                    claimToken = $"claim-{attemptText}",
                    attempt = attemptText,
                    claimedAt = timestamp,
                    expiresAt,
                };
            }
            return JsonSerializer.Serialize(action);
        }

        private static string ErrorBody(string code, string message) =>
            JsonSerializer.Serialize(new { error = new { code, message } });

        private static async Task<RuntimeRequest> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            using var header = new MemoryStream();
            byte[] terminator = [13, 10, 13, 10];
            int matched = 0;
            byte[] next = new byte[1];
            while (matched < terminator.Length)
            {
                int read = await stream.ReadAsync(next, cancellationToken);
                if (read == 0) throw new IOException("HTTP request ended before its headers.");
                int value = next[0];
                header.WriteByte((byte)value);
                matched = value == terminator[matched]
                    ? matched + 1
                    : value == terminator[0] ? 1 : 0;
            }

            string headerText = Encoding.ASCII.GetString(header.ToArray());
            string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string path = lines[0].Split(' ')[1];
            string contentLength = lines
                .Skip(1)
                .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))?
                .Split(':', 2)[1]
                .Trim() ?? "0";
            int length = int.Parse(contentLength, CultureInfo.InvariantCulture);
            byte[] body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return new RuntimeRequest(path, Encoding.UTF8.GetString(body));
        }

        private static async Task RespondAsync(
            NetworkStream stream,
            HttpStatusCode status,
            string body,
            string contentType,
            CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string headers =
                $"HTTP/1.1 {(int)status} {status}\r\n"
                + $"Content-Type: {contentType}\r\n"
                + $"Content-Length: {bytes.Length}\r\n"
                + "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
        }

        private sealed record RuntimeRequest(string Path, string Body);
    }
}
