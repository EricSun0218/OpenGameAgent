using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World.Tests;

public sealed class NativeWorldSettlementCompositionTests
{
    private static readonly string[] FixtureFiles =
    {
        "world.json",
        "clocks.json",
        "numerics.json",
        "events.json",
        "interactions.json",
        "agents.json",
        "knowledge.json"
    };

    [Fact]
    public async Task EvidenceReadsOnlyAppliedReceiptFromActiveGeneration()
    {
        await using var session = new NativeWorldEngineSession();
        var loaded = await session.LoadPackageAsync(FixtureArchive());
        var initial = Require(await session.ReadSnapshotAsync());
        var saveBeforeReceipt = await session.CaptureSaveBytesAsync();
        var receipt = await ExecuteAsync(
            session,
            loaded.Package!,
            initial,
            "evidence");
        var source = new NativeWorldCommittedEvidenceSource(session);

        var read = await session.ReadReceiptAsync(receipt.ReceiptId);
        var evidence = await source.ReadCommittedAsync(receipt.ReceiptId);

        Assert.NotNull(read);
        Assert.Equal(loaded.Generation, read!.Generation);
        Assert.Equal(receipt.ReceiptId, read.Receipt.ReceiptId);
        Assert.NotNull(evidence);
        Assert.Equal(
            receipt.ReceiptId,
            evidence!.Source.WorldReceiptId);
        Assert.Null(await source.ReadCommittedAsync(new string('f', 64)));

        var rejected = await CreateRejectedReceiptAsync(session);
        var cancelled = await CreateCancelledReceiptAsync(session);
        Assert.Null(await source.ReadCommittedAsync(rejected.ReceiptId));
        Assert.Null(await source.ReadCommittedAsync(cancelled.ReceiptId));

        var replacement = await session.LoadSaveAsync(saveBeforeReceipt);

        Assert.Equal(2, replacement.Generation);
        Assert.Null(await source.ReadCommittedAsync(receipt.ReceiptId));
    }

    [Fact]
    public async Task CallerEvidenceCannotReplaceNativeLedgerEvidence()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "fake");
        var real = fixture.Evidence;
        var fake = new CommittedWorldPresentationEvidence(
            real.Source,
            real.Binding,
            WorldPresentationCommitStatus.Applied,
            "caller_claimed_outcome",
            Json("""{"callerEvidence":true}"""));
        var plan = PrivatePlan(
            "fake-evidence-settlement",
            fake,
            new GameEntityIdentity("mira", 1));
        var outbox = new InMemoryWorldSettlementStore();
        var coordinator =
            new NativeWorldSettlementComposition(session)
                .CreateCoordinator(
                    outbox,
                    memory: new DeterministicMemoryStore());

        var exception =
            await Assert.ThrowsAsync<WorldSettlementEvidenceException>(
                async () => await coordinator.SettleAsync(plan));

        Assert.Equal(
            WorldSettlementReasonCodes.EvidenceMismatch,
            exception.ReasonCode);
        Assert.Null(await outbox.ReadAsync(plan.SettlementId));
    }

    [Fact]
    public async Task MaximumLengthOperationReceiptSettlesFromLedger()
    {
        await using var session = new NativeWorldEngineSession();
        await session.LoadPackageAsync(FixtureArchive());
        var operationId = new string('o', 192);
        var receipt = await CreateAppliedReceiptAsync(
            session,
            operationId);
        var composition = new NativeWorldSettlementComposition(session);
        var evidence = await composition.EvidenceSource
            .ReadCommittedAsync(receipt.ReceiptId);

        Assert.NotNull(evidence);
        Assert.Equal(operationId, evidence!.Source.OperationId);
        var result = await composition.CreateCoordinator(
                new InMemoryWorldSettlementStore(),
                memory: new DeterministicMemoryStore())
            .SettleAsync(
                PrivatePlan(
                    "maximum-operation-settlement",
                    evidence,
                    new GameEntityIdentity("mira", 1)));

        Assert.Equal(WorldSettlementStage.Applied, result.Stage);
    }

    [Fact]
    public async Task TimedReceiptSettlesWithAuthoritativeGameTime()
    {
        await using var session = new NativeWorldEngineSession();
        await session.LoadPackageAsync(FixtureArchive());
        var snapshot = Require(await session.ReadSnapshotAsync());
        var authoritativeTime = new GameTimePoint(
            "calendar.month",
            snapshot.Coordinate.TimelineId,
            snapshot.Coordinate.TimelineEpoch,
            tick: 17);
        var receipt = await CreateAppliedReceiptAsync(
            session,
            "timed-operation",
            authoritativeTime);
        var composition = new NativeWorldSettlementComposition(session);
        var evidence = await composition.EvidenceSource
            .ReadCommittedAsync(receipt.ReceiptId);

        Assert.NotNull(evidence);
        Assert.NotNull(evidence!.Binding.GameTime);
        Assert.Equal(
            authoritativeTime.ClockId,
            evidence.Binding.GameTime!.ClockId);
        Assert.Equal(
            authoritativeTime.TimelineId,
            evidence.Binding.GameTime.TimelineId);
        Assert.Equal(
            authoritativeTime.Epoch,
            evidence.Binding.GameTime.Epoch);
        Assert.Equal(
            authoritativeTime.Tick,
            evidence.Binding.GameTime.Tick);

        var result = await composition.CreateCoordinator(
                new InMemoryWorldSettlementStore(),
                memory: new DeterministicMemoryStore())
            .SettleAsync(
                PrivatePlan(
                    "timed-receipt-settlement",
                    evidence,
                    new GameEntityIdentity("mira", 1)));

        Assert.Equal(WorldSettlementStage.Applied, result.Stage);
    }

    [Fact]
    public async Task ExactLeaseRejectsEveryCoordinateAndDigestMismatch()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "binding");
        var binding = fixture.Evidence.Binding;
        var mismatches = new[]
        {
            CloneBinding(binding, worldId: "another-world"),
            CloneBinding(binding, timelineId: "another-timeline"),
            CloneBinding(
                binding,
                timelineEpoch: binding.TimelineEpoch + 1),
            CloneBinding(
                binding,
                saveRevision: binding.SaveRevision + 1),
            CloneBinding(
                binding,
                stateVersion: binding.StateVersion + 1),
            CloneBinding(binding, catalogDigest: new string('a', 64)),
            CloneBinding(
                binding,
                committedStateDigest: new string('b', 64))
        };

        foreach (var mismatch in mismatches)
        {
            await using var denied =
                await session.AcquireSettlementLeaseAsync(mismatch);
            Assert.Null(denied);
            Assert.True(session.Status.IsAcceptingOperations);
        }

        await using var exact =
            await session.AcquireSettlementLeaseAsync(binding);

        Assert.NotNull(exact);
        Assert.Equal(session.Status.Generation, exact!.Generation);
        Assert.Equal(
            fixture.Receipt.ReceiptId,
            (await exact.ReadReceiptAsync(fixture.Receipt.ReceiptId))!
            .Receipt.ReceiptId);
    }

    [Fact]
    public async Task LeaseDrainsWorkAndBlocksAdmissionAndReplacement()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "lease");
        var save = await session.CaptureSaveBytesAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = session.RunAsync(
                "native-settlement-held-operation",
                authoritative: true,
                async (_, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return true;
                })
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var acquisition = session.AcquireSettlementLeaseAsync(
                fixture.Evidence.Binding)
            .AsTask();
        await WaitUntilAsync(
            () => !session.Status.IsAcceptingOperations,
            TimeSpan.FromSeconds(2));
        Assert.False(acquisition.IsCompleted);

        release.TrySetResult();
        Assert.True(await running);
        await using var lease =
            await acquisition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(lease);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.RunAsync(
                "blocked-authoritative-operation",
                authoritative: true,
                static (_, _) => new ValueTask<bool>(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.LoadSaveAsync(save));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.LoadPackageAsync(FixtureArchive()));
        Assert.Equal(1, session.Status.Generation);

        await lease!.DisposeAsync();
        Assert.True(session.Status.IsAcceptingOperations);
        Assert.NotNull(await session.ReadSnapshotAsync());
    }

    [Fact]
    public async Task CancellationAndRepeatedAcquireReleaseTheFence()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "cancel");
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = session.RunAsync(
                "native-settlement-cancel-held",
                authoritative: true,
                async (_, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return true;
                })
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var cancelled = session.AcquireSettlementLeaseAsync(
                fixture.Evidence.Binding,
                cancellation.Token)
            .AsTask();
        await WaitUntilAsync(
            () => !session.Status.IsAcceptingOperations,
            TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await cancelled);
        Assert.True(session.Status.IsAcceptingOperations);
        release.TrySetResult();
        Assert.True(await running);

        var guard = new NativeWorldSettlementAuthorityGuard(session);
        var request = new WorldSettlementAuthorityRequest(
            PrivatePlan(
                "repeated-acquire",
                fixture.Evidence,
                new GameEntityIdentity("mira", 1)));
        var first = await guard.AcquireAsync(request);
        Assert.NotNull(first);
        var secondTask = guard.AcquireAsync(request).AsTask();
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        await first!.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
        await second!.DisposeAsync();
        Assert.True(session.Status.IsAcceptingOperations);
    }

    [Fact]
    public async Task OperationCallbackCannotAcquireItsOwnSettlementLease()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(
            session,
            "reentrant-lease");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.RunAsync(
                    "reentrant-settlement-acquire",
                    authoritative: true,
                    async (runtime, cancellationToken) =>
                    {
                        _ = runtime;
                        await session.AcquireSettlementLeaseAsync(
                            fixture.Evidence.Binding,
                            cancellationToken);
                        return true;
                    })
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(session.Status.IsAcceptingOperations);
        Assert.Equal(0, session.Status.ActiveOperations);
    }

    [Fact]
    public async Task SettlementLeaseFactoryFailuresReleaseTheFence()
    {
        var factoryCalls = 0;
        await using var session = new NativeWorldEngineSession(
            options: null,
            saveBridge: null,
            settlementLeaseFactory:
            (
                owner,
                runtime,
                leaseId,
                generation,
                snapshot) =>
            {
                var call = Interlocked.Increment(ref factoryCalls);
                if (call == 1)
                {
                    throw new InvalidOperationException(
                        "settlement_lease_factory_fault");
                }
                if (call == 2)
                {
                    return null!;
                }

                return new NativeWorldEngineSettlementLease(
                    owner,
                    runtime,
                    leaseId,
                    generation,
                    snapshot);
            });
        await session.LoadPackageAsync(FixtureArchive());
        var initial = Require(await session.ReadSnapshotAsync());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.AcquireSettlementLeaseAsync(
                    BindingFor(initial))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("settlement_lease_factory_fault", exception.Message);
        Assert.True(session.Status.IsAcceptingOperations);
        var replacement = await session.LoadPackageAsync(FixtureArchive())
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(replacement.Activated);
        Assert.Equal(2, replacement.Generation);
        var current = Require(await session.ReadSnapshotAsync());
        var nullException =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await session.AcquireSettlementLeaseAsync(
                        BindingFor(current))
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains(
            "settlement lease factory returned no lease",
            nullException.Message);
        Assert.True(session.Status.IsAcceptingOperations);
        replacement = await session.LoadPackageAsync(FixtureArchive())
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(replacement.Activated);
        Assert.Equal(3, replacement.Generation);
        current = Require(await session.ReadSnapshotAsync());
        await using var reacquired =
            await session.AcquireSettlementLeaseAsync(BindingFor(current))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(reacquired);
        Assert.Equal(3, factoryCalls);
    }

    [Fact]
    public async Task OperationCallbackCannotStartDrainingTransitions()
    {
        await using var session = new NativeWorldEngineSession();
        var archive = FixtureArchive();
        await session.LoadPackageAsync(archive);
        var save = await session.CaptureSaveBytesAsync();
        var packagePath = Path.GetTempFileName();
        var savePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(packagePath, archive);
            await File.WriteAllBytesAsync(savePath, save);

            await AssertOperationCallbackTransitionFailsFastAsync(
                session,
                "reentrant-load-package",
                () => session.LoadPackageAsync(archive).AsTask());
            await AssertOperationCallbackTransitionFailsFastAsync(
                session,
                "reentrant-load-package-file",
                () => session.LoadPackageFileAsync(packagePath).AsTask());
            await AssertOperationCallbackTransitionFailsFastAsync(
                session,
                "reentrant-load-save",
                () => session.LoadSaveAsync(save).AsTask());
            await AssertOperationCallbackTransitionFailsFastAsync(
                session,
                "reentrant-load-save-file",
                () => session.LoadSaveFileAsync(savePath).AsTask());
            await AssertOperationCallbackTransitionFailsFastAsync(
                session,
                "reentrant-shutdown",
                () => session.ShutdownAsync().AsTask());
        }
        finally
        {
            File.Delete(packagePath);
            File.Delete(savePath);
        }

        Assert.Equal(1, session.Status.Generation);
        Assert.True(session.Status.IsAcceptingOperations);
        Assert.Equal(0, session.Status.ActiveOperations);
    }

    [Fact]
    public async Task ConcurrentInteractionAndLeaseAcquisitionDoNotDeadlock()
    {
        await using var session = new NativeWorldEngineSession();
        var loaded = await session.LoadPackageAsync(FixtureArchive());
        var initial = Require(await session.ReadSnapshotAsync());
        var planned = await session.PlanInteractionAsync(
            Interaction(initial, loaded.Package!.CatalogDigest, "race"));
        var interaction =
            Assert.IsType<NativeWorldEnginePlannedInteraction>(
                planned.Value);
        var binding = new WorldPresentationBinding(
            initial.Coordinate.WorldId,
            initial.Coordinate.TimelineId,
            initial.Coordinate.TimelineEpoch,
            initial.Coordinate.SaveRevision,
            initial.Coordinate.StateVersion,
            initial.Coordinate.CatalogDigest,
            committedStateDigest: initial.StateDigest);
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = Task.Run(
            async () =>
            {
                await start.Task;
                try
                {
                    return await session.ExecuteInteractionAsync(
                        interaction);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            });
        var acquisition = Task.Run(
            async () =>
            {
                await start.Task;
                return await session.AcquireSettlementLeaseAsync(binding);
            });

        start.TrySetResult();
        await Task.WhenAll(execution, acquisition)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var executionResult = await execution;
        var lease = await acquisition;
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }

        Assert.True(
            executionResult is not null || lease is not null);
        Assert.True(session.Status.IsAcceptingOperations);
    }

    [Fact]
    public async Task PrivateAudienceAppliesButIncarnationMismatchFails()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "private");
        var composition = new NativeWorldSettlementComposition(session);
        var memory = new DeterministicMemoryStore();
        var outbox = new InMemoryWorldSettlementStore();
        var allowed = PrivatePlan(
            "private-allowed",
            fixture.Evidence,
            new GameEntityIdentity("mira", 1));
        var stale = PrivatePlan(
            "private-stale",
            fixture.Evidence,
            new GameEntityIdentity("mira", 2));

        var allowedResult = await composition.CreateCoordinator(
                outbox,
                memory: memory)
            .SettleAsync(allowed);
        var staleResult = await composition.CreateCoordinator(
                outbox,
                memory: memory)
            .SettleAsync(stale);

        Assert.Equal(WorldSettlementStage.Applied, allowedResult.Stage);
        Assert.Equal(WorldSettlementStage.Rejected, staleResult.Stage);
        Assert.Contains(
            staleResult.DeliveryStates,
            item => item.ReasonCode
                    == NativeWorldSettlementReasonCodes
                        .IncarnationMismatch);
    }

    [Fact]
    public async Task LeaseRejectsSameOperationAndKindTampering()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "claim-digest");
        var original = PrivatePlan(
            "claim-original",
            fixture.Evidence,
            new GameEntityIdentity("mira", 1));
        var originalDelivery =
            Assert.IsType<WorldSettlementMemoryDelivery>(
                original.Deliveries[0]);
        var audienceChanged = new WorldSettlementMemoryDelivery(
            originalDelivery.OperationId,
            new WorldSettlementAudienceClaim(
                "actor:ren",
                membershipRevision: 0,
                new[] { new GameEntityIdentity("ren", 1) },
                WorldSettlementPrivacyClasses.Private,
                "none"),
            originalDelivery.Mutations);
        var payloadChanged = PrivatePlan(
            "claim-payload-changed",
            fixture.Evidence,
            new GameEntityIdentity("mira", 1));
        var guard = new NativeWorldSettlementAuthorityGuard(session);
        await using var lease = await guard.AcquireAsync(
            new WorldSettlementAuthorityRequest(original));

        Assert.NotNull(lease);
        var originalDecision = await lease!.ValidateAsync(
            ClaimFor(originalDelivery));
        var audienceDecision = await lease.ValidateAsync(
            ClaimFor(audienceChanged));
        var payloadDecision = await lease.ValidateAsync(
            ClaimFor(payloadChanged.Deliveries[0]));

        Assert.True(originalDecision.Accepted);
        Assert.False(audienceDecision.Accepted);
        Assert.False(payloadDecision.Accepted);
        Assert.Equal(
            NativeWorldSettlementReasonCodes.ClaimMismatch,
            audienceDecision.ReasonCode);
        Assert.Equal(
            NativeWorldSettlementReasonCodes.ClaimMismatch,
            payloadDecision.ReasonCode);
        Assert.NotEqual(
            originalDelivery.SemanticDigest,
            audienceChanged.SemanticDigest);
        Assert.NotEqual(
            originalDelivery.SemanticDigest,
            payloadChanged.Deliveries[0].SemanticDigest);
    }

    [Fact]
    public async Task GroupAudienceFailsClosedWithoutPolicy()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "group-deny");
        var plan = GroupPlan(
            "group-no-policy",
            fixture.Evidence);
        var groupStore = new InMemoryGroupInteractionStore();
        await CreateGroupAsync(groupStore, fixture.Evidence.Binding);
        var coordinator =
            new NativeWorldSettlementComposition(session)
                .CreateCoordinator(
                    new InMemoryWorldSettlementStore(),
                    groups: groupStore);

        var result = await coordinator.SettleAsync(plan);

        Assert.Equal(WorldSettlementStage.Rejected, result.Stage);
        Assert.Contains(
            result.DeliveryStates,
            item => item.ReasonCode
                    == NativeWorldSettlementReasonCodes
                        .AudiencePolicyRequired);
    }

    [Fact]
    public async Task PolicyCanDenyOrLeaseAndAllowGroupAudience()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "group-policy");
        var deniedPolicy = new RecordingPolicy(allow: false);
        var denied = await new NativeWorldSettlementComposition(
                session,
                deniedPolicy)
            .CreateCoordinator(
                new InMemoryWorldSettlementStore(),
                memory: new DeterministicMemoryStore())
            .SettleAsync(
                PrivatePlan(
                    "policy-denied",
                    fixture.Evidence,
                    new GameEntityIdentity("mira", 1)));

        Assert.Equal(WorldSettlementStage.Rejected, denied.Stage);
        Assert.Equal(1, deniedPolicy.AcquireCount);
        Assert.Equal(1, deniedPolicy.ValidateCount);
        Assert.Equal(1, deniedPolicy.DisposeCount);

        var groupStore = new InMemoryGroupInteractionStore();
        var groupPlan = GroupPlan("policy-allowed", fixture.Evidence);
        await CreateGroupAsync(
            groupStore,
            fixture.Evidence.Binding);
        var allowedPolicy = new RecordingPolicy(allow: true);
        var allowed = await new NativeWorldSettlementComposition(
                session,
                allowedPolicy)
            .CreateCoordinator(
                new InMemoryWorldSettlementStore(),
                groups: groupStore)
            .SettleAsync(groupPlan);

        Assert.Equal(WorldSettlementStage.Applied, allowed.Stage);
        Assert.Equal(1, allowedPolicy.AcquireCount);
        Assert.Equal(1, allowedPolicy.ValidateCount);
        Assert.Equal(1, allowedPolicy.DisposeCount);
        Assert.Single(
            (await groupStore.ReadAsync("settlement-group-session"))!
            .Messages);
    }

    [Fact]
    public async Task PolicyCallbackMutationFailsFastWithoutDeadlock()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(session, "no-deadlock");
        var policy = new MutationAttemptPolicy(
            session,
            FixtureArchive());
        var coordinator = new NativeWorldSettlementComposition(
                session,
                policy)
            .CreateCoordinator(
                new InMemoryWorldSettlementStore(),
                memory: new DeterministicMemoryStore());

        var result = await coordinator.SettleAsync(
                PrivatePlan(
                    "callback-no-deadlock",
                    fixture.Evidence,
                    new GameEntityIdentity("mira", 1)))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorldSettlementStage.Applied, result.Stage);
        Assert.True(policy.MutationWasBlocked);
        Assert.Equal(1, session.Status.Generation);
        Assert.True(session.Status.IsAcceptingOperations);
    }

    [Fact]
    public async Task PolicyAcquireCannotReenterSettlementLease()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(
            session,
            "policy-acquire-reentry");
        var policy = new ReentrantSettlementAcquirePolicy(
            session,
            attemptDuringAcquire: true);
        var coordinator = new NativeWorldSettlementComposition(
                session,
                policy)
            .CreateCoordinator(
                new InMemoryWorldSettlementStore(),
                memory: new DeterministicMemoryStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.SettleAsync(
                    PrivatePlan(
                        "policy-acquire-reentry",
                        fixture.Evidence,
                        new GameEntityIdentity("mira", 1)))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(policy.AttemptedAcquire);
        Assert.True(session.Status.IsAcceptingOperations);
        await using var lease =
            await session.AcquireSettlementLeaseAsync(
                fixture.Evidence.Binding);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task PolicyValidationCannotReenterSettlementLease()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(
            session,
            "policy-validate-reentry");
        var policy = new ReentrantSettlementAcquirePolicy(
            session,
            attemptDuringAcquire: false);
        var coordinator = new NativeWorldSettlementComposition(
                session,
                policy)
            .CreateCoordinator(
                new InMemoryWorldSettlementStore(),
                memory: new DeterministicMemoryStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.SettleAsync(
                    PrivatePlan(
                        "policy-validate-reentry",
                        fixture.Evidence,
                        new GameEntityIdentity("mira", 1)))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(policy.AttemptedAcquire);
        Assert.Equal(1, policy.DisposeCount);
        Assert.True(session.Status.IsAcceptingOperations);
        await using var lease =
            await session.AcquireSettlementLeaseAsync(
                fixture.Evidence.Binding);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task PolicyDisposeReentryFailureIsStableAcrossDisposals()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(
            session,
            "policy-dispose-reentry");
        var policy = new ReentrantSettlementDisposePolicy(session);
        var guard = new NativeWorldSettlementAuthorityGuard(
            session,
            policy);
        var authorityLease = await guard.AcquireAsync(
            new WorldSettlementAuthorityRequest(
                PrivatePlan(
                    "policy-dispose-reentry",
                    fixture.Evidence,
                    new GameEntityIdentity("mira", 1))));

        Assert.NotNull(authorityLease);
        var firstDisposal = authorityLease!.DisposeAsync().AsTask();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await firstDisposal
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("settlement policy callback", exception.Message);
        Assert.True(policy.ReentryWasBlocked);
        Assert.Equal(1, policy.DisposeCount);
        var secondDisposal = authorityLease.DisposeAsync().AsTask();
        Assert.Same(firstDisposal, secondDisposal);
        var repeated = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await secondDisposal
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(exception, repeated);
        Assert.Equal(1, policy.DisposeCount);
        Assert.True(session.Status.IsAcceptingOperations);
        await using var reacquired =
            await session.AcquireSettlementLeaseAsync(
                    fixture.Evidence.Binding)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task ConcurrentDisposalsShareCleanupAndFailure()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(
            session,
            "concurrent-policy-dispose");
        var expectedFailure = new InvalidOperationException(
            "controlled_policy_dispose_failure");
        var policy = new ControlledDisposePolicy(expectedFailure);
        var guard = new NativeWorldSettlementAuthorityGuard(
            session,
            policy);
        var authorityLease = await guard.AcquireAsync(
            new WorldSettlementAuthorityRequest(
                PrivatePlan(
                    "concurrent-policy-dispose",
                    fixture.Evidence,
                    new GameEntityIdentity("mira", 1))));

        Assert.NotNull(authorityLease);
        var first = authorityLease!.DisposeAsync().AsTask();
        await policy.DisposeEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var second = authorityLease.DisposeAsync().AsTask();
        Assert.Same(first, second);
        var reacquisition = session.AcquireSettlementLeaseAsync(
                fixture.Evidence.Binding)
            .AsTask();
        try
        {
            await Task.Delay(50);
            Assert.False(first.IsCompleted);
            Assert.False(reacquisition.IsCompleted);
            Assert.Equal(1, policy.DisposeCount);
        }
        finally
        {
            policy.ReleaseDispose();
        }

        var firstFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await first
                    .WaitAsync(TimeSpan.FromSeconds(2)));
        var secondFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await second
                    .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(expectedFailure, firstFailure);
        Assert.Same(firstFailure, secondFailure);
        Assert.Equal(1, policy.DisposeCount);
        var reacquired =
            await reacquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
        Assert.True(session.Status.IsAcceptingOperations);
    }

    [Fact]
    public async Task FireAndForgetDisposalFaultIsObservedInternally()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(
            session,
            "fire-and-forget-policy-dispose");
        var expectedFailure = new InvalidOperationException(
            "fire_and_forget_policy_dispose_failure");
        var policy = new ControlledDisposePolicy(expectedFailure);
        var guard = new NativeWorldSettlementAuthorityGuard(
            session,
            policy);
        IWorldSettlementAuthorityLease? authorityLease =
            await guard.AcquireAsync(
                new WorldSettlementAuthorityRequest(
                    PrivatePlan(
                        "fire-and-forget-policy-dispose",
                        fixture.Evidence,
                        new GameEntityIdentity("mira", 1))));
        Assert.NotNull(authorityLease);
        var matchingUnobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler =
            (_, eventArgs) =>
            {
                if (eventArgs.Exception
                    .Flatten()
                    .InnerExceptions
                    .Any(
                        exception => ReferenceEquals(
                            exception,
                            expectedFailure)))
                {
                    Interlocked.Exchange(
                        ref matchingUnobserved,
                        1);
                    eventArgs.SetObserved();
                }
            };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var disposalTask = FireAndForgetDispose(authorityLease!);
            authorityLease = null;
            await policy.DisposeEntered.WaitAsync(
                TimeSpan.FromSeconds(2));
            policy.ReleaseDispose();
            await WaitUntilAsync(
                () => session.Status.IsAcceptingOperations,
                TimeSpan.FromSeconds(2));

            await ForceCollectionAsync(disposalTask);

            Assert.False(disposalTask.IsAlive);
            Assert.Equal(
                0,
                Volatile.Read(ref matchingUnobserved));
            Assert.Equal(1, policy.DisposeCount);
        }
        finally
        {
            policy.ReleaseDispose();
            TaskScheduler.UnobservedTaskException -= handler;
            if (authorityLease is not null)
            {
                try
                {
                    await authorityLease.DisposeAsync();
                }
                catch (InvalidOperationException)
                {
                    // The policy's expected disposal fault is observed.
                }
            }
        }
    }

    [Fact]
    public async Task AcquireFailurePolicyCleanupCannotReenter()
    {
        await using var session = new NativeWorldEngineSession();
        var fixture = await CreateReceiptFixtureAsync(
            session,
            "policy-acquire-cleanup-reentry");
        var request = new WorldSettlementAuthorityRequest(
            PrivatePlan(
                "policy-acquire-cleanup-reentry",
                fixture.Evidence,
                new GameEntityIdentity("mira", 1)));
        FaultFirstDeliveryAfterRequestValidation(request);
        var policy = new ReentrantSettlementDisposePolicy(session);
        var guard = new NativeWorldSettlementAuthorityGuard(
            session,
            policy);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await guard.AcquireAsync(request)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("settlement policy callback", exception.Message);
        Assert.Equal(1, policy.AcquireCount);
        Assert.Equal(1, policy.DisposeCount);
        Assert.True(policy.ReentryWasBlocked);
        Assert.True(session.Status.IsAcceptingOperations);
        await using var reacquired =
            await session.AcquireSettlementLeaseAsync(
                    fixture.Evidence.Binding)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(reacquired);
    }

    private static async ValueTask<ReceiptFixture>
        CreateReceiptFixtureAsync(
            NativeWorldEngineSession session,
            string suffix)
    {
        var loaded = await session.LoadPackageAsync(FixtureArchive());
        var initial = Require(await session.ReadSnapshotAsync());
        var receipt = await ExecuteAsync(
            session,
            loaded.Package!,
            initial,
            suffix);
        var source = new NativeWorldCommittedEvidenceSource(session);
        var evidence = await source.ReadCommittedAsync(receipt.ReceiptId);
        return new ReceiptFixture(
            receipt,
            Assert.IsType<CommittedWorldPresentationEvidence>(
                evidence));
    }

    private static async ValueTask<WorldCommandReceipt> ExecuteAsync(
        NativeWorldEngineSession session,
        ActivatedWorldPackage package,
        WorldAuthoritativeStateSnapshot snapshot,
        string suffix)
    {
        var planned = await session.PlanInteractionAsync(
            Interaction(snapshot, package.CatalogDigest, suffix));
        var executed = await session.ExecuteInteractionAsync(
            Assert.IsType<NativeWorldEnginePlannedInteraction>(
                planned.Value));
        var execution = Assert.Single(executed.Value!.Executions);
        return Assert.IsType<WorldCommandReceipt>(
            execution.Result.Receipt);
    }

    private static async ValueTask<WorldCommandReceipt>
        CreateRejectedReceiptAsync(NativeWorldEngineSession session)
    {
        return await session.RunAsync(
            "create-rejected-receipt",
            authoritative: true,
            async (runtime, cancellationToken) =>
            {
                var snapshot = Require(
                    await runtime.ReadSnapshotAsync(cancellationToken));
                var coordinate = snapshot.Coordinate;
                var stale = new WorldAuthoritativeCoordinate(
                    coordinate.WorldId,
                    coordinate.TimelineId,
                    coordinate.TimelineEpoch,
                    coordinate.SaveRevision,
                    coordinate.StateVersion == 0
                        ? 1
                        : coordinate.StateVersion - 1,
                    coordinate.CatalogDigest);
                var begin = await runtime.TransactionStore.BeginAsync(
                    new WorldTransactionRequest(
                        "rejected-operation",
                        "rejected-command",
                        new string('c', 64),
                        stale),
                    cancellationToken);
                return Assert.IsType<WorldCommandReceipt>(begin.Receipt);
            });
    }

    private static async ValueTask<WorldCommandReceipt>
        CreateCancelledReceiptAsync(NativeWorldEngineSession session)
    {
        return await session.RunAsync(
            "create-cancelled-receipt",
            authoritative: true,
            async (runtime, cancellationToken) =>
            {
                var snapshot = Require(
                    await runtime.ReadSnapshotAsync(cancellationToken));
                var request = new WorldTransactionRequest(
                    "cancelled-operation",
                    "cancelled-command",
                    new string('d', 64),
                    snapshot.Coordinate);
                var begin = await runtime.TransactionStore.BeginAsync(
                    request,
                    cancellationToken);
                var transaction =
                    Assert.IsAssignableFrom<IWorldAuthoritativeTransaction>(
                        begin.Transaction);
                var cancelled =
                    await runtime.TransactionStore.CancelPendingAsync(
                        request.ExpectedCoordinate.Scope,
                        request.OperationId,
                        request.RequestFingerprint,
                        "cancelled_for_test",
                        cancellationToken);
                await transaction.DisposeAsync();
                return Assert.IsType<WorldCommandReceipt>(
                    cancelled.Receipt);
            });
    }

    private static async ValueTask<WorldCommandReceipt>
        CreateAppliedReceiptAsync(
            NativeWorldEngineSession session,
            string operationId,
            GameTimePoint? occurredAt = null)
    {
        return await session.RunAsync(
            "create-applied-ledger-receipt",
            authoritative: true,
            async (runtime, cancellationToken) =>
            {
                var snapshot = Require(
                    await runtime.ReadSnapshotAsync(cancellationToken));
                var coordinate = snapshot.Coordinate;
                var occurrence = new WorldEventHistoryRecord(
                    "maximum-operation-event",
                    new WorldEventDefinitionKey(
                        coordinate.WorldId,
                        coordinate.TimelineId,
                        coordinate.TimelineEpoch,
                        "manual-event",
                        "1"),
                    "manual-trigger",
                    "manual-resolution",
                    new string('e', 64),
                    occurredAt,
                    parentInstanceId: null);
                var request = new WorldTransactionRequest(
                    operationId,
                    "maximum-operation-command",
                    new string('f', 64),
                    coordinate,
                    new[]
                    {
                        new WorldEntityIncarnationExpectation(
                            "mira",
                            1)
                    },
                    occurrence);
                var begin = await runtime.TransactionStore.BeginAsync(
                    request,
                    cancellationToken);
                await using var transaction =
                    Assert.IsAssignableFrom<
                        IWorldAuthoritativeTransaction>(
                        begin.Transaction);
                var committed = await transaction.CommitEventAsync(
                    new WorldEffectReceipt(
                        applied: true,
                        outcomeCode: "world_action_applied"),
                    cancellationToken);
                return Assert.IsType<WorldCommandReceipt>(
                    committed.Receipt);
            });
    }

    private static WorldSettlementPlan PrivatePlan(
        string settlementId,
        CommittedWorldPresentationEvidence evidence,
        GameEntityIdentity owner)
    {
        var binding = evidence.Binding;
        var source = evidence.Source;
        var scopeId = "actor:" + owner.EntityId;
        var record = new MemoryRecord(
            settlementId + "-memory",
            scopeId,
            Json("""{"kind":"native-settlement-test"}"""),
            tags: null,
            importance: 50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                binding.WorldId,
                sessionId: null,
                binding.SaveRevision,
                "native-settlement-test",
                source.WorldReceiptId,
                committed: true,
                binding.TimelineId,
                new GameKnowledgePerspective(owner, "observed"),
                binding.TimelineEpoch),
            gameTimeWindow: binding.GameTime is null
                ? null
                : new GameTimeWindow(binding.GameTime));
        return new WorldSettlementPlan(
            settlementId,
            evidence,
            new WorldSettlementDelivery[]
            {
                new WorldSettlementMemoryDelivery(
                    "memory-operation",
                    new WorldSettlementAudienceClaim(
                        scopeId,
                        membershipRevision: 0,
                        new[] { owner },
                        WorldSettlementPrivacyClasses.Private,
                        "none"),
                    new[] { MemoryMutation.Upsert(record) })
            });
    }

    private static WorldSettlementPlan GroupPlan(
        string settlementId,
        CommittedWorldPresentationEvidence evidence)
    {
        var members = GroupMembers();
        const string operationId = "group-operation";
        var request = new GroupInteractionAppendRequest(
            operationId,
            "settlement-group-session",
            expectedRevision: 0,
            expectedMembershipRevision: 0,
            new[]
            {
                new GroupInteractionMessageDraft(
                    settlementId + "-message",
                    "world.notice",
                    Json("""{"kind":"native-settlement-group-test"}"""),
                    GroupInteractionAudienceModes.AllMembers,
                    author: members[0].Actor,
                    causationId: evidence.Source.WorldReceiptId)
            });
        return new WorldSettlementPlan(
            settlementId,
            evidence,
            new WorldSettlementDelivery[]
            {
                new WorldSettlementGroupDelivery(
                    operationId,
                    "settlement-group",
                    members,
                    request)
            });
    }

    private static async ValueTask CreateGroupAsync(
        InMemoryGroupInteractionStore store,
        WorldPresentationBinding binding)
    {
        var result = await store.CreateAsync(
            new GroupInteractionCreateRequest(
                "create-settlement-group",
                "settlement-group-session",
                "settlement-group",
                Json("""{"kind":"native-settlement-test"}"""),
                GroupMembers(),
                new GroupInteractionWorldBinding(
                    binding.WorldId,
                    binding.TimelineId,
                    binding.TimelineEpoch,
                    binding.SaveRevision)));
        Assert.Equal(
            GroupInteractionWriteStatuses.Applied,
            result.Status);
    }

    private static GroupInteractionMember[] GroupMembers()
    {
        return new[]
        {
            new GroupInteractionMember(
                new GameEntityIdentity("mira", 1)),
            new GroupInteractionMember(
                new GameEntityIdentity("ren", 1))
        };
    }

    private static InteractionExecutionRequest Interaction(
        WorldAuthoritativeStateSnapshot snapshot,
        string catalogDigest,
        string suffix)
    {
        var coordinate = snapshot.Coordinate;
        return new InteractionExecutionRequest(
            "native-settlement-interaction-" + suffix,
            "native-settlement-operation-" + suffix,
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(CultureInfo.InvariantCulture),
            catalogDigest,
            "offer-garden-help",
            "1",
            new GameEntityIdentity("mira", 1),
            new[] { new GameEntityIdentity("ren", 1) },
            "local",
            Json("""{"topic":"garden"}"""));
    }

    private static WorldPresentationBinding CloneBinding(
        WorldPresentationBinding source,
        string? worldId = null,
        string? timelineId = null,
        long? timelineEpoch = null,
        long? saveRevision = null,
        long? stateVersion = null,
        string? catalogDigest = null,
        string? committedStateDigest = null)
    {
        return new WorldPresentationBinding(
            worldId ?? source.WorldId,
            timelineId ?? source.TimelineId,
            timelineEpoch ?? source.TimelineEpoch,
            saveRevision ?? source.SaveRevision,
            stateVersion ?? source.StateVersion,
            catalogDigest ?? source.CatalogDigest,
            source.GameTime,
            committedStateDigest ?? source.CommittedStateDigest);
    }

    private static WorldPresentationBinding BindingFor(
        WorldAuthoritativeStateSnapshot snapshot)
    {
        var coordinate = snapshot.Coordinate;
        return new WorldPresentationBinding(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion,
            coordinate.CatalogDigest,
            committedStateDigest: snapshot.StateDigest);
    }

    private static WorldSettlementDeliveryClaim ClaimFor(
        WorldSettlementDelivery delivery)
    {
        var constructor = typeof(WorldSettlementDeliveryClaim)
            .GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        return Assert.IsType<WorldSettlementDeliveryClaim>(
            constructor.Invoke(new object[] { delivery }));
    }

    private static void FaultFirstDeliveryAfterRequestValidation(
        WorldSettlementAuthorityRequest request)
    {
        // Fault injection reaches the defensive cleanup branch after the
        // policy lease has been acquired. Public construction has already
        // validated and cloned the otherwise immutable request.
        var deliveries = request.Plan.Deliveries;
        var backingField = deliveries.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(
                field => typeof(IList<WorldSettlementDelivery>)
                    .IsAssignableFrom(field.FieldType));
        var backing = Assert.IsAssignableFrom<
            IList<WorldSettlementDelivery>>(
            backingField.GetValue(deliveries));
        backing[0] = null!;
    }

    private static byte[] FixtureArchive()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "fixtures",
            "world-v1",
            "interactive-smoke");
        var definition = new WorldPackageDefinition(
            "interactive-smoke-fixture",
            "1",
            FixtureFiles.Select(
                fileName => new WorldPackageFile(
                    fileName,
                    "application/json",
                    File.ReadAllBytes(
                        Path.Combine(directory, fileName)))));
        using var stream = new MemoryStream();
        WorldPackageArchive.Write(stream, definition);
        return stream.ToArray();
    }

    private static WorldAuthoritativeStateSnapshot Require(
        WorldAuthoritativeStateSnapshot? snapshot)
    {
        return snapshot
               ?? throw new InvalidOperationException(
                   "The session returned no authoritative state.");
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "GameAgentRuntime.sln"))
                && Directory.Exists(
                    Path.Combine(
                        directory.FullName,
                        "fixtures",
                        "world-v1",
                        "interactive-smoke")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the interactive-world fixture.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var started = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow - started >= timeout)
            {
                throw new TimeoutException(
                    "The expected session state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FireAndForgetDispose(
        IWorldSettlementAuthorityLease authorityLease)
    {
        var disposal = authorityLease.DisposeAsync().AsTask();
        return new WeakReference(disposal);
    }

    private static async Task ForceCollectionAsync(
        WeakReference reference)
    {
        for (var attempt = 0;
             attempt < 20 && reference.IsAlive;
             attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
    }

    private static async Task
        AssertOperationCallbackTransitionFailsFastAsync(
            NativeWorldEngineSession session,
            string operationId,
            Func<Task> transition)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.RunAsync(
                    operationId,
                    authoritative: true,
                    async (_, _) =>
                    {
                        await transition().ConfigureAwait(false);
                        return true;
                    })
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class ReceiptFixture
    {
        public ReceiptFixture(
            WorldCommandReceipt receipt,
            CommittedWorldPresentationEvidence evidence)
        {
            Receipt = receipt;
            Evidence = evidence;
        }

        public WorldCommandReceipt Receipt { get; }

        public CommittedWorldPresentationEvidence Evidence { get; }
    }

    private sealed class RecordingPolicy
        : INativeWorldSettlementAudiencePolicy
    {
        private readonly bool _allow;

        public RecordingPolicy(bool allow)
        {
            _allow = allow;
        }

        public int AcquireCount { get; private set; }

        public int ValidateCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>
            AcquireAsync(
                NativeWorldSettlementPolicyRequest request,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            Assert.True(request.SessionGeneration > 0);
            return new ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>(
                new Lease(this));
        }

        private sealed class Lease
            : INativeWorldSettlementAudiencePolicyLease
        {
            private RecordingPolicy? _owner;

            public Lease(RecordingPolicy owner)
            {
                _owner = owner;
            }

            public ValueTask<WorldSettlementAuthorityDecision>
                ValidateAsync(
                    WorldSettlementDeliveryClaim claim,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var owner = _owner
                            ?? throw new ObjectDisposedException(
                                nameof(Lease));
                owner.ValidateCount++;
                return new ValueTask<
                    WorldSettlementAuthorityDecision>(
                    owner._allow
                        ? WorldSettlementAuthorityDecision.Allow()
                        : WorldSettlementAuthorityDecision.Deny(
                            "game_policy_denied"));
            }

            public ValueTask DisposeAsync()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    owner.DisposeCount++;
                }

                return default;
            }
        }
    }

    private sealed class MutationAttemptPolicy
        : INativeWorldSettlementAudiencePolicy
    {
        private readonly NativeWorldEngineSession _session;
        private readonly byte[] _archive;

        public MutationAttemptPolicy(
            NativeWorldEngineSession session,
            byte[] archive)
        {
            _session = session;
            _archive = archive;
        }

        public bool MutationWasBlocked { get; private set; }

        public ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>
            AcquireAsync(
                NativeWorldSettlementPolicyRequest request,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>(
                new Lease(this));
        }

        private sealed class Lease
            : INativeWorldSettlementAudiencePolicyLease
        {
            private MutationAttemptPolicy? _owner;

            public Lease(MutationAttemptPolicy owner)
            {
                _owner = owner;
            }

            public async ValueTask<WorldSettlementAuthorityDecision>
                ValidateAsync(
                    WorldSettlementDeliveryClaim claim,
                    CancellationToken cancellationToken = default)
            {
                var owner = _owner
                            ?? throw new ObjectDisposedException(
                                nameof(Lease));
                try
                {
                    await owner._session.LoadPackageAsync(
                        owner._archive,
                        cancellationToken: cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    owner.MutationWasBlocked = true;
                }

                return WorldSettlementAuthorityDecision.Allow();
            }

            public ValueTask DisposeAsync()
            {
                _owner = null;
                return default;
            }
        }
    }

    private sealed class ReentrantSettlementAcquirePolicy
        : INativeWorldSettlementAudiencePolicy
    {
        private readonly bool _attemptDuringAcquire;
        private readonly NativeWorldEngineSession _session;

        public ReentrantSettlementAcquirePolicy(
            NativeWorldEngineSession session,
            bool attemptDuringAcquire)
        {
            _session = session;
            _attemptDuringAcquire = attemptDuringAcquire;
        }

        public bool AttemptedAcquire { get; private set; }

        public int DisposeCount { get; private set; }

        public async ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>
            AcquireAsync(
                NativeWorldSettlementPolicyRequest request,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_attemptDuringAcquire)
            {
                await AttemptAcquireAsync(request.Request.Binding)
                    .ConfigureAwait(false);
            }

            return new Lease(this, request.Request.Binding);
        }

        private async ValueTask AttemptAcquireAsync(
            WorldPresentationBinding binding)
        {
            AttemptedAcquire = true;
            await using var lease =
                await _session.AcquireSettlementLeaseAsync(binding)
                    .ConfigureAwait(false);
        }

        private sealed class Lease
            : INativeWorldSettlementAudiencePolicyLease
        {
            private readonly WorldPresentationBinding _binding;
            private ReentrantSettlementAcquirePolicy? _owner;

            public Lease(
                ReentrantSettlementAcquirePolicy owner,
                WorldPresentationBinding binding)
            {
                _owner = owner;
                _binding = binding;
            }

            public async ValueTask<WorldSettlementAuthorityDecision>
                ValidateAsync(
                    WorldSettlementDeliveryClaim claim,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var owner = _owner
                            ?? throw new ObjectDisposedException(
                                nameof(Lease));
                if (!owner._attemptDuringAcquire)
                {
                    await owner.AttemptAcquireAsync(_binding)
                        .ConfigureAwait(false);
                }

                return WorldSettlementAuthorityDecision.Allow();
            }

            public ValueTask DisposeAsync()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    owner.DisposeCount++;
                }

                return default;
            }
        }
    }

    private sealed class ReentrantSettlementDisposePolicy
        : INativeWorldSettlementAudiencePolicy
    {
        private readonly NativeWorldEngineSession _session;

        public ReentrantSettlementDisposePolicy(
            NativeWorldEngineSession session)
        {
            _session = session;
        }

        public int AcquireCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool ReentryWasBlocked { get; private set; }

        public ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>
            AcquireAsync(
                NativeWorldSettlementPolicyRequest request,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            return new ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>(
                new Lease(this, request.Request.Binding));
        }

        private sealed class Lease
            : INativeWorldSettlementAudiencePolicyLease
        {
            private readonly WorldPresentationBinding _binding;
            private ReentrantSettlementDisposePolicy? _owner;

            public Lease(
                ReentrantSettlementDisposePolicy owner,
                WorldPresentationBinding binding)
            {
                _owner = owner;
                _binding = binding;
            }

            public ValueTask<WorldSettlementAuthorityDecision>
                ValidateAsync(
                    WorldSettlementDeliveryClaim claim,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = _owner
                    ?? throw new ObjectDisposedException(nameof(Lease));
                return new ValueTask<WorldSettlementAuthorityDecision>(
                    WorldSettlementAuthorityDecision.Allow());
            }

            public async ValueTask DisposeAsync()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null)
                {
                    return;
                }

                owner.DisposeCount++;
                try
                {
                    await using var nested =
                        await owner._session
                            .AcquireSettlementLeaseAsync(_binding)
                            .ConfigureAwait(false);
                }
                catch (InvalidOperationException exception)
                    when (exception.Message.Contains(
                        "settlement policy callback",
                        StringComparison.Ordinal))
                {
                    owner.ReentryWasBlocked = true;
                    throw;
                }
            }
        }
    }

    private sealed class ControlledDisposePolicy
        : INativeWorldSettlementAudiencePolicy
    {
        private readonly Exception _failure;
        private readonly TaskCompletionSource _disposeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public ControlledDisposePolicy(Exception failure)
        {
            _failure = failure;
        }

        public Task DisposeEntered => _disposeEntered.Task;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>
            AcquireAsync(
                NativeWorldSettlementPolicyRequest request,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<
                INativeWorldSettlementAudiencePolicyLease?>(
                new Lease(this));
        }

        public void ReleaseDispose()
        {
            _releaseDispose.TrySetResult();
        }

        private sealed class Lease
            : INativeWorldSettlementAudiencePolicyLease
        {
            private ControlledDisposePolicy? _owner;

            public Lease(ControlledDisposePolicy owner)
            {
                _owner = owner;
            }

            public ValueTask<WorldSettlementAuthorityDecision>
                ValidateAsync(
                    WorldSettlementDeliveryClaim claim,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = _owner
                    ?? throw new ObjectDisposedException(nameof(Lease));
                return new ValueTask<WorldSettlementAuthorityDecision>(
                    WorldSettlementAuthorityDecision.Allow());
            }

            public async ValueTask DisposeAsync()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null)
                {
                    return;
                }

                Interlocked.Increment(ref owner._disposeCount);
                owner._disposeEntered.TrySetResult();
                await owner._releaseDispose.Task.ConfigureAwait(false);
                throw owner._failure;
            }
        }
    }
}
