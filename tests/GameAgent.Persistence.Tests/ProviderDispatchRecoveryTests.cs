using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class ProviderDispatchRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactWireEvidenceAvailabilityRecovers(bool available)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-wire-recovery-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var route = ExactWireRoute();
            var evidence = available
                ? ProviderWireRequestEvidence.CreateAvailable(
                    Encoding.UTF8.GetBytes("""{"messages":[]}"""),
                    "application/json",
                    route)
                : ProviderWireRequestEvidence.CreateUnavailable(
                    route,
                    "provider_wire_evidence_unavailable");

            await CommitDispatchAsync(
                journal,
                run,
                route,
                evidence);

            var recovered = await new RunRecovery(store, store, journal)
                .LoadAsync(run.RunId, default);

            var dispatch = Assert.Single(
                recovered!.UnsettledProviderDispatches);
            Assert.Equal(route.ProviderId, dispatch.ProviderId);
            Assert.Equal(route.RouteDigest, dispatch.ProviderRouteDigest);
            Assert.Equal(
                route.DialectSemanticDigest,
                dispatch.ProviderDialectSemanticDigest);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("content_type")]
    [InlineData("provider")]
    [InlineData("route")]
    [InlineData("dialect_semantic")]
    public async Task TamperedExactWireEvidenceIsRejectedAsCorruption(
        string mutation)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-wire-tamper-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var route = ExactWireRoute();
            var evidence = ProviderWireRequestEvidence.CreateAvailable(
                Encoding.UTF8.GetBytes("""{"messages":[]}"""),
                "application/json",
                route);
            await CommitDispatchAsync(journal, run, route, evidence);

            var tamperedStore = new MutatingReadSessionStore(
                store,
                runtimeEvent =>
                {
                    if (!string.Equals(
                            runtimeEvent.Kind,
                            RuntimeEventKinds.ProviderDispatchStarted,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    var wire = runtimeEvent.Extensions[
                        ProviderWireRequestEvidence.JournalExtensionName];
                    runtimeEvent.Extensions[
                            ProviderWireRequestEvidence.JournalExtensionName] =
                        mutation switch
                        {
                            "hash" => ReplaceJsonProperty(
                                wire,
                                "payloadSha256",
                                JsonSerializer.SerializeToElement(
                                    new string('b', 64))),
                            "length" => ReplaceJsonProperty(
                                wire,
                                "payloadByteLength",
                                JsonSerializer.SerializeToElement(999)),
                            "content_type" => ReplaceJsonProperty(
                                wire,
                                "contentType",
                                JsonSerializer.SerializeToElement(
                                    "application/problem+json")),
                            "provider" => ReplaceJsonProperty(
                                wire,
                                "providerId",
                                JsonSerializer.SerializeToElement(
                                    "provider-other")),
                            "route" => ReplaceJsonProperty(
                                wire,
                                "providerRouteDigest",
                                JsonSerializer.SerializeToElement(
                                    new string('b', 64))),
                            _ => ReplaceJsonProperty(
                                wire,
                                "dialectSemanticDigest",
                                JsonSerializer.SerializeToElement(
                                    new string('c', 64)))
                        };
                });

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(tamperedStore, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("transcript_attempt")]
    [InlineData("transcript_provider")]
    [InlineData("result_attempt")]
    [InlineData("result_stream")]
    [InlineData("result_provider")]
    [InlineData("completion_attempt")]
    [InlineData("completion_stream")]
    [InlineData("completion_provider")]
    public async Task ProviderCompletionChainIdentityMismatchIsRejected(
        string mutation)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-completion-identity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var route = ExactWireRoute();
            await CommitDispatchAsync(
                journal,
                run,
                route,
                ProviderWireRequestEvidence.CreateUnavailable(route));
            await CommitUsageAsync(journal, run, route);
            var assistant = NormalizedTranscript.AssistantResponse(
                "assistant-final",
                """{"value":"done"}""",
                reasoningContent: null,
                Array.Empty<ModelToolCall>(),
                clock.UtcNow);
            await journal.CommitFinalCompletionAsync(
                run,
                assistant,
                Json("""{"value":"done"}"""),
                "turn-1",
                route.ProviderId,
                "attempt-1",
                "stream-1",
                clock.UtcNow,
                default,
                route);

            var baseline = await new RunRecovery(store, store, journal)
                .LoadAsync(run.RunId, default);
            Assert.True(baseline!.FinalOutput.HasValue);

            var tamperedStore = new MutatingReadSessionStore(
                store,
                runtimeEvent =>
                {
                    var isTranscript = string.Equals(
                        runtimeEvent.Kind,
                        RuntimeEventKinds.TranscriptMessage,
                        StringComparison.Ordinal);
                    var isResult = string.Equals(
                        runtimeEvent.Kind,
                        RuntimeEventKinds.ProviderResultCommitted,
                        StringComparison.Ordinal);
                    var isCompletion = string.Equals(
                        runtimeEvent.Kind,
                        RuntimeEventKinds.AssistantCompleted,
                        StringComparison.Ordinal);
                    if (mutation == "transcript_attempt" && isTranscript
                        || mutation == "result_attempt" && isResult
                        || mutation == "completion_attempt" && isCompletion)
                    {
                        runtimeEvent.AttemptId = "attempt-other";
                    }
                    else if (mutation == "result_stream" && isResult
                             || mutation == "completion_stream"
                             && isCompletion)
                    {
                        runtimeEvent.StreamAttemptId = "stream-other";
                    }
                    else if (mutation == "transcript_provider"
                             && isTranscript
                             || mutation == "result_provider" && isResult
                             || mutation == "completion_provider"
                             && isCompletion)
                    {
                        runtimeEvent.ProviderId = "provider-other";
                    }
                });

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(tamperedStore, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("result_route")]
    [InlineData("opaque_route")]
    [InlineData("opaque_without_result_route")]
    public async Task ProviderResultRouteOrOpaqueEnvelopeMismatchIsRejected(
        string mutation)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-result-route-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var route = ExactWireRoute(
                opaqueContinuationStateVersion: "test.state.v1");
            await CommitDispatchAsync(
                journal,
                run,
                route,
                ProviderWireRequestEvidence.CreateUnavailable(route));
            await CommitUsageAsync(journal, run, route);
            var state = ProviderOpaqueContinuationState.Bind(
                route,
                new ProviderOpaqueContinuationUpdate(
                    "test.state.v1",
                    Json("""{"cursor":"next"}"""),
                    ProviderOpaqueStatePersistence.DurableNonSecret));
            var assistant = NormalizedTranscript.AssistantResponse(
                "assistant-result",
                "continue",
                reasoningContent: null,
                Array.Empty<ModelToolCall>(),
                clock.UtcNow);
            await journal.CommitProviderResultAsync(
                run,
                assistant,
                "turn-1",
                route.ProviderId,
                "attempt-1",
                "stream-1",
                default,
                route,
                state);

            var baseline = await new RunRecovery(store, store, journal)
                .LoadAsync(run.RunId, default);
            Assert.NotNull(baseline);
            Assert.Equal(
                state.PayloadDigest,
                baseline!.ProviderOpaqueContinuationState?.PayloadDigest);

            var tamperedStore = new MutatingReadSessionStore(
                store,
                runtimeEvent =>
                {
                    if (!string.Equals(
                            runtimeEvent.Kind,
                            RuntimeEventKinds.ProviderResultCommitted,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (mutation == "result_route")
                    {
                        runtimeEvent.ProviderRouteDigest =
                            new string('d', 64);
                        return;
                    }

                    if (mutation == "opaque_without_result_route")
                    {
                        runtimeEvent.ModelId = null;
                        runtimeEvent.TransportDialect = null;
                        runtimeEvent.ProviderCapabilityDigest = null;
                        runtimeEvent.ProviderRouteDigest = null;
                        runtimeEvent.Extensions.Remove(
                            ProviderRouteJournalExtensions.PolicyVersion);
                        runtimeEvent.Extensions.Remove(
                            ProviderRouteJournalExtensions.PolicyDigest);
                        runtimeEvent.Extensions.Remove(
                            ProviderWireRequestEvidence
                                .DialectSemanticDigestJournalExtensionName);
                        return;
                    }

                    var envelope = runtimeEvent.Extensions[
                        ProviderOpaqueContinuationState.JournalExtensionName];
                    runtimeEvent.Extensions[
                            ProviderOpaqueContinuationState
                                .JournalExtensionName] =
                        ReplaceJsonProperty(
                            envelope,
                            "providerRouteDigest",
                            JsonSerializer.SerializeToElement(
                                new string('e', 64)));
                });

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(tamperedStore, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("output_digest")]
    [InlineData("attempt")]
    [InlineData("stream")]
    [InlineData("provider")]
    public async Task RuntimeFallbackCompletionEvidenceTamperingIsRejected(
        string mutation)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-runtime-completion-evidence-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            await journal.CommitCompletionAsync(
                run,
                Json("""{"value":"fallback"}"""),
                "turn-1",
                "attempt-1",
                "stream-1",
                clock.UtcNow,
                default);

            var baseline = await new RunRecovery(store, store, journal)
                .LoadAsync(run.RunId, default);
            Assert.True(baseline!.FinalOutput.HasValue);
            Assert.Equal(RunStates.Completed, baseline.Run.State);

            var tamperedStore = new MutatingReadSessionStore(
                store,
                runtimeEvent =>
                {
                    if (!string.Equals(
                            runtimeEvent.Kind,
                            RuntimeEventKinds.AssistantCompleted,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (mutation == "missing")
                    {
                        runtimeEvent.Extensions.Remove(
                            RuntimeCompletionEvidence.ExtensionName);
                    }
                    else if (mutation == "provider")
                    {
                        runtimeEvent.ProviderId = "provider-injected";
                    }
                    else
                    {
                        var evidence = runtimeEvent.Extensions[
                            RuntimeCompletionEvidence.ExtensionName];
                        runtimeEvent.Extensions[
                                RuntimeCompletionEvidence.ExtensionName] =
                            mutation switch
                            {
                                "output_digest" => ReplaceJsonProperty(
                                    evidence,
                                    "outputDigest",
                                    JsonSerializer.SerializeToElement(
                                        new string('f', 64))),
                                "attempt" => ReplaceJsonProperty(
                                    evidence,
                                    "attemptId",
                                    JsonSerializer.SerializeToElement(
                                        "attempt-other")),
                                _ => ReplaceJsonProperty(
                                    evidence,
                                    "streamAttemptId",
                                    JsonSerializer.SerializeToElement(
                                        "stream-other"))
                            };
                    }
                });

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(tamperedStore, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialOrTamperedRoutePolicyIsRejectedAsCorruption(
        bool partial)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-policy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var metadata = new ProviderRouteMetadata(
                "model-1",
                "test.streaming.v1",
                "test.route-policy.v1",
                new string('c', 64));
            var identity = new ProviderRouteIdentity(
                "provider-1",
                metadata,
                new ProviderCapabilities());
            var dispatch = ProviderEvent(
                run,
                RuntimeEventKinds.ProviderDispatchStarted,
                identity.ProviderId,
                clock.UtcNow);
            dispatch.ModelId = identity.ModelId;
            dispatch.TransportDialect = identity.TransportDialect;
            dispatch.ProviderCapabilityDigest =
                identity.CapabilityDigest;
            dispatch.ProviderRouteDigest = identity.RouteDigest;
            dispatch.Extensions[
                ProviderRouteJournalExtensions.PolicyVersion] =
                JsonSerializer.SerializeToElement(
                    identity.RoutePolicyVersion);
            if (!partial)
            {
                dispatch.Extensions[
                    ProviderRouteJournalExtensions.PolicyDigest] =
                    JsonSerializer.SerializeToElement(
                        new string('d', 64));
            }

            await store.AppendAtomicAsync(dispatch, run.Revision);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(store, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialOrTamperedProviderRouteIsRejectedAsCorruption(
        bool partial)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-route-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var dispatch = ProviderEvent(
                run,
                RuntimeEventKinds.ProviderDispatchStarted,
                "provider-1",
                clock.UtcNow);
            dispatch.ModelId = "model-1";
            if (!partial)
            {
                dispatch.TransportDialect = "test.streaming.v1";
                dispatch.ProviderCapabilityDigest = new string('a', 64);
                dispatch.ProviderRouteDigest = new string('b', 64);
            }

            await store.AppendAtomicAsync(dispatch, run.Revision);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(store, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyRouteDigestWithoutPolicyExtensionsStillRecovers()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-legacy-route-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var dispatch = ProviderEvent(
                run,
                RuntimeEventKinds.ProviderDispatchStarted,
                "provider-1",
                clock.UtcNow);
            dispatch.ModelId = "model-1";
            dispatch.TransportDialect = "test.streaming.v1";
            dispatch.ProviderCapabilityDigest = new string('a', 64);
            dispatch.ProviderRouteDigest =
                ProviderRouteIdentity.ComputeRouteDigest(
                    dispatch.ProviderId!,
                    dispatch.ModelId!,
                    dispatch.TransportDialect!,
                    dispatch.ProviderCapabilityDigest!);
            await store.AppendAtomicAsync(dispatch, run.Revision);

            var recovered = await new RunRecovery(store, store, journal)
                .LoadAsync(run.RunId, default);

            var pending = Assert.Single(
                recovered!.UnsettledProviderDispatches);
            Assert.Null(pending.ProviderRoutePolicyVersion);
            Assert.Null(pending.ProviderRoutePolicyDigest);
            Assert.Equal(
                dispatch.ProviderRouteDigest,
                pending.ProviderRouteDigest);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MismatchedProviderSettlementIsRejectedAsCorruption()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-identity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var run = CreateRun(clock.UtcNow);
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var dispatchAppend = await store.AppendAtomicAsync(
                ProviderEvent(
                    run,
                    RuntimeEventKinds.ProviderDispatchStarted,
                    "provider-1",
                    clock.UtcNow),
                run.Revision);
            run.Revision = dispatchAppend.Revision;
            await store.AppendAtomicAsync(
                ProviderEvent(
                    run,
                    RuntimeEventKinds.BudgetUpdated,
                    "provider-2",
                    clock.UtcNow),
                run.Revision);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RunRecovery(store, store, journal)
                    .LoadAsync(run.RunId, default)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BilledResultMissingFailsClosedWithoutRedispatch(
        bool emitToolCall)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-result-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore = new RejectAfterProviderUsageStore(store);
            var firstProvider = new SuccessfulProvider(emitToolCall);
            var clock = new Clock();
            var ids = new Ids();
            var run = CreateRun(clock.UtcNow);

            await using (var firstRuntime = CreateRuntime(
                             crashStore,
                             crashStore,
                             firstProvider,
                             clock,
                             ids))
            {
                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest { Run = run });

                Assert.Equal(RunStates.Running, interrupted.Run.State);
                Assert.Equal(1, firstProvider.CallCount);
                Assert.True(crashStore.RejectedPostUsageAppend);
            }

            var beforeRecovery = await store.ReadRunAsync(run.RunId, default);
            var usage = Assert.Single(
                beforeRecovery,
                item => item.Kind == RuntimeEventKinds.BudgetUpdated
                        && !string.IsNullOrWhiteSpace(
                            item.StreamAttemptId));
            Assert.DoesNotContain(
                beforeRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderResultCommitted
                        || item.Kind
                        == RuntimeEventKinds.ProviderResultDiscarded);
            Assert.DoesNotContain(
                beforeRecovery,
                item => item.Kind == RuntimeEventKinds.TranscriptMessage
                        && NormalizedMessageJournalCodec
                            .Decode(item.Payload)
                            .Role == NormalizedRoles.Assistant);

            var secondProvider = new NeverInvokedProvider();
            await using var recoveredRuntime = CreateRuntime(
                store,
                store,
                secondProvider,
                clock,
                ids);

            var recovered = await recoveredRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, recovered.Run.State);
            Assert.Equal(
                "provider_result_recovery_required",
                recovered.ErrorCode);
            Assert.Equal("provider", recovered.ErrorCategory);
            Assert.Equal(1, recovered.Run.Usage.ProviderUsageSamples);
            Assert.Equal(1, recovered.Run.Usage.CacheReadTokens);
            Assert.Equal(0, recovered.Run.Usage.CacheWriteTokens);
            Assert.Equal(2, recovered.Run.Usage.CacheMissTokens);
            Assert.Equal(1, recovered.Run.Usage.ReasoningTokens);
            Assert.Equal(5, recovered.Run.Usage.ProviderTotalTokens);
            Assert.Equal(
                UsageAvailabilityStates.CostAvailable,
                recovered.Run.Usage.Availability);
            Assert.False(recovered.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(0, secondProvider.CallCount);

            var repeated = await recoveredRuntime.ResumeAsync(run.RunId);
            Assert.Equal(RunStates.Failed, repeated.Run.State);
            Assert.Equal(0, secondProvider.CallCount);

            var afterRecovery = await store.ReadRunAsync(run.RunId, default);
            Assert.Single(
                afterRecovery,
                item => item.Kind == RuntimeEventKinds.RunFailed);
            Assert.DoesNotContain(
                afterRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);
            Assert.Equal(firstProvider.ProviderId, usage.ProviderId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnsettledDispatchFailsClosedBeforeAnotherProviderCall()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-provider-dispatch-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore = new RejectAfterDispatchStore(store);
            var firstProvider = new DispatchThenFailProvider();
            var clock = new Clock();
            var ids = new Ids();
            var run = CreateRun(clock.UtcNow);

            await using (var firstRuntime = CreateRuntime(
                             crashStore,
                             crashStore,
                             firstProvider,
                             clock,
                             ids))
            {
                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest { Run = run });

                Assert.Equal(RunStates.Running, interrupted.Run.State);
                Assert.Equal(1, firstProvider.CallCount);
                Assert.True(crashStore.RejectedPostDispatchAppend);
            }

            var beforeRecovery = await store.ReadRunAsync(run.RunId, default);
            var dispatch = Assert.Single(
                beforeRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderDispatchStarted);
            Assert.Equal(firstProvider.ProviderId, dispatch.ProviderId);
            Assert.Equal(
                firstProvider.RouteMetadata.ModelId,
                dispatch.ModelId);
            Assert.Equal(
                firstProvider.RouteMetadata.TransportDialect,
                dispatch.TransportDialect);
            Assert.Equal(64, dispatch.ProviderCapabilityDigest!.Length);
            Assert.Equal(64, dispatch.ProviderRouteDigest!.Length);
            Assert.Equal(
                firstProvider.RouteMetadata.RoutePolicyVersion,
                dispatch.Extensions[
                    ProviderRouteJournalExtensions.PolicyVersion]
                    .GetString());
            Assert.Equal(
                firstProvider.RouteMetadata.RoutePolicyDigest,
                dispatch.Extensions[
                    ProviderRouteJournalExtensions.PolicyDigest]
                    .GetString());
            Assert.False(string.IsNullOrWhiteSpace(dispatch.AttemptId));
            Assert.False(string.IsNullOrWhiteSpace(
                dispatch.StreamAttemptId));
            Assert.DoesNotContain(
                beforeRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderDispatchKnownZero
                        || item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain
                        || item.Kind == RuntimeEventKinds.BudgetUpdated
                           && item.StreamAttemptId
                           == dispatch.StreamAttemptId);

            var secondProvider = new NeverInvokedProvider();
            await using var recoveredRuntime = CreateRuntime(
                store,
                store,
                secondProvider,
                clock,
                ids);

            var recovered = await recoveredRuntime.ResumeAsync(run.RunId);

            Assert.Equal(RunStates.Failed, recovered.Run.State);
            Assert.Equal(
                "provider_usage_reconciliation_required",
                recovered.ErrorCode);
            Assert.Equal("billing", recovered.ErrorCategory);
            Assert.True(recovered.Run.Usage.HasUnaccountedUsage);
            Assert.Equal(
                1,
                recovered.Run.Usage.UnaccountedProviderAttempts);
            Assert.Equal(0, secondProvider.CallCount);

            var afterRecovery = await store.ReadRunAsync(run.RunId, default);
            var uncertain = Assert.Single(
                afterRecovery,
                item => item.Kind
                        == RuntimeEventKinds.ProviderUsageUncertain);
            Assert.Equal(dispatch.ProviderId, uncertain.ProviderId);
            Assert.Equal(dispatch.AttemptId, uncertain.AttemptId);
            Assert.Equal(
                dispatch.StreamAttemptId,
                uncertain.StreamAttemptId);
            Assert.Equal(
                "provider_dispatch_recovery_unknown",
                uncertain.ReasonCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProviderRouteIdentity ExactWireRoute(
        string? opaqueContinuationStateVersion = null)
    {
        var dialect = new ProviderDialectContract(
            "test.chat-completions.v1",
            ProviderRequestFamily.ChatCompletions,
            "test.request.v1",
            ProviderStreamFraming.ServerSentEvents,
            "test.sse.v1",
            "test.tools.v1",
            "test.usage.v1",
            "test.reasoning.v1",
            "application/json",
            opaqueContinuationStateVersion);
        return new ProviderRouteIdentity(
            "provider-1",
            new ProviderRouteMetadata(
                "model-1",
                dialect,
                "test.route-policy.v1",
                new string('a', 64)),
            new ProviderCapabilities
            {
                Streaming = true,
                ToolCalling = true,
                JsonOutput = true,
                MaxContextTokens = 100_000
            });
    }

    private static ValueTask CommitDispatchAsync(
        JournalCoordinator journal,
        AgentRun run,
        ProviderRouteIdentity route,
        ProviderWireRequestEvidence evidence)
    {
        var wireEvidence = evidence.ToJson();
        return journal.CommitRunMutationAsync(
            run,
            RuntimeEventKinds.ProviderDispatchStarted,
            _ => { },
            "turn-1",
            "attempt-1",
            default,
            eventId: "provider-dispatch:stream-1",
            streamAttemptId: "stream-1",
            providerId: route.ProviderId,
            modelId: route.ModelId,
            transportDialect: route.TransportDialect,
            providerCapabilityDigest: route.CapabilityDigest,
            providerRouteDigest: route.RouteDigest,
            eventExtensions:
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [ProviderWireRequestEvidence.JournalExtensionName] =
                        wireEvidence,
                    [ProviderWireRequestEvidence
                            .DialectSemanticDigestJournalExtensionName] =
                        JsonSerializer.SerializeToElement(
                            route.DialectSemanticDigest),
                    [ProviderDialectContract.JournalExtensionName] =
                        route.DialectContract.ToJson(),
                    [ProviderWireRequestEvidence
                            .IntegrityDigestJournalExtensionName] =
                        JsonSerializer.SerializeToElement(
                            CanonicalJsonDigest.ComputeSha256(wireEvidence)),
                    [ProviderRouteJournalExtensions.PolicyVersion] =
                        JsonSerializer.SerializeToElement(
                            route.RoutePolicyVersion),
                    [ProviderRouteJournalExtensions.PolicyDigest] =
                        JsonSerializer.SerializeToElement(
                            route.RoutePolicyDigest)
                });
    }

    private static ValueTask CommitUsageAsync(
        JournalCoordinator journal,
        AgentRun run,
        ProviderRouteIdentity route)
    {
        return journal.CommitRunMutationAsync(
            run,
            RuntimeEventKinds.BudgetUpdated,
            _ => { },
            "turn-1",
            "attempt-1",
            default,
            eventId: "provider-usage:stream-1",
            streamAttemptId: "stream-1",
            providerId: route.ProviderId);
    }

    private static JsonElement ReplaceJsonProperty(
        JsonElement source,
        string propertyName,
        JsonElement replacement)
    {
        var properties = source.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
        properties[propertyName] = replacement.Clone();
        return JsonSerializer.SerializeToElement(properties);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static DurableAgentRuntime CreateRuntime(
        IDurableSessionStore store,
        IOperationLedger operations,
        IStreamingModelProvider provider,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids)
    {
        var journal = new JournalCoordinator(
            store,
            operations,
            clock,
            ids);
        return new DurableAgentRuntime(
            new ProviderAttemptRunner(
                new[] { provider },
                new ProviderRetryPolicy
                {
                    MaxAttemptsPerProvider = 1,
                    IdleTimeout = TimeSpan.FromSeconds(2),
                    TotalTimeout = TimeSpan.FromSeconds(5)
                },
                new SystemRuntimeDelay(),
                ids),
            new Host(),
            journal,
            new RunRecovery(store, operations, journal),
            new ToolCatalogRegistry(),
            new SkillCatalogRegistry(),
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            clock,
            ids,
            new DurableAgentRuntimeOptions
            {
                ModelId = "test-model",
                MaxConcurrentProviderCalls = 1
            });
    }

    private static AgentRun CreateRun(DateTimeOffset now)
    {
        return new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Queued,
            Budget = new AgentBudget
            {
                MaxTurns = 4,
                MaxDurationMs = 10_000,
                MaxTokens = 2_000,
                MaxCostUsd = "1",
                MaxActions = 0
            },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static RuntimeEvent ProviderEvent(
        AgentRun run,
        string kind,
        string providerId,
        DateTimeOffset timestamp)
    {
        var checkpoint = ProtocolJson.DeserializeAgentRun(
            ProtocolJson.Serialize(run));
        checkpoint.Revision = checked(run.Revision + 1);
        return new RuntimeEvent
        {
            EventId = kind + "-" + Guid.NewGuid().ToString("N"),
            RunId = run.RunId,
            TurnId = "turn-1",
            Kind = kind,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = run.RuntimeGeneration,
            AttemptId = "provider-attempt-1",
            StreamAttemptId = "stream-attempt-1",
            ProviderId = providerId,
            Timestamp = timestamp,
            Payload = ProtocolJson.ToElement(checkpoint)
        };
    }

    private sealed class DispatchThenFailProvider :
        IStreamingModelProvider,
        IProviderRouteMetadataSource
    {
        private int _callCount;

        public string ProviderId => "dispatch-then-fail";

        public ProviderRouteMetadata RouteMetadata { get; } =
            new(
                "dispatch-model",
                "test.dispatch.v1",
                "test.dispatch-policy.v1",
                new string('c', 64));

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            throw new IOException("Simulated process loss after dispatch.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class NeverInvokedProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "never-invoked";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            throw new InvalidOperationException(
                "Recovery must not dispatch another provider request.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class SuccessfulProvider : IStreamingModelProvider
    {
        private readonly bool _emitToolCall;
        private int _callCount;

        public SuccessfulProvider(bool emitToolCall)
        {
            _emitToolCall = emitToolCall;
        }

        public string ProviderId => "successful-provider";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            if (_emitToolCall)
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = "tool-call-1",
                    ToolNameDelta = "unknown_tool",
                    ArgumentsJsonDelta = "{}"
                };
            }
            else
            {
                yield return new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = 0,
                    Kind = ModelStreamEventKinds.TextDelta,
                    TextDelta = """{"value":"done"}"""
                };
            }

            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 3,
                    OutputTokens = 2,
                    CostUsd = "0.001",
                    CacheReadTokens = 1,
                    CacheWriteTokens = 0,
                    CacheMissTokens = 2,
                    ReasoningTokens = 1,
                    ProviderTotalTokens = 5
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = _emitToolCall ? "tool_calls" : "stop"
            };
        }
    }

    private sealed class Host : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new InvalidOperationException("No tool call expected.");
        }
    }

    private sealed class Clock : IRuntimeClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class Ids : IRuntimeIdGenerator
    {
        public string NewId(string category)
        {
            return category + "-" + Guid.NewGuid().ToString("N");
        }
    }

    private sealed class MutatingReadSessionStore : IDurableSessionStore
    {
        private readonly IDurableSessionStore _inner;
        private readonly Action<RuntimeEvent> _mutation;

        public MutatingReadSessionStore(
            IDurableSessionStore inner,
            Action<RuntimeEvent> mutation)
        {
            _inner = inner;
            _mutation = mutation;
        }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public async ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            var source = await _inner.ReadRunAsync(
                    runId,
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = new RuntimeEvent[source.Count];
            for (var index = 0; index < source.Count; index++)
            {
                snapshot[index] = ProtocolJson.DeserializeRuntimeEvent(
                    ProtocolJson.Serialize(source[index]));
                _mutation(snapshot[index]);
            }

            return snapshot;
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetRunCursorAsync(runId, cancellationToken);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }

    private sealed class RejectAfterDispatchStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private int _dispatchCommitted;

        public RejectAfterDispatchStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public bool RejectedPostDispatchAppend { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            RejectIfDispatchWasCommitted();
            var result = await _inner.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind
                == RuntimeEventKinds.ProviderDispatchStarted)
            {
                Volatile.Write(ref _dispatchCommitted, 1);
            }

            return result;
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            RejectIfDispatchWasCommitted();
            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetRunCursorAsync(runId, cancellationToken);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetOperationAsync(operationId, cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(runId, cancellationToken);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        private void RejectIfDispatchWasCommitted()
        {
            if (Volatile.Read(ref _dispatchCommitted) == 0)
            {
                return;
            }

            RejectedPostDispatchAppend = true;
            throw new IOException(
                "Simulated process loss after provider dispatch.");
        }
    }

    private sealed class RejectAfterProviderUsageStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private int _usageCommitted;

        public RejectAfterProviderUsageStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public bool RejectedPostUsageAppend { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            RejectIfUsageWasCommitted();
            var result = await _inner.AppendAtomicAsync(
                    runtimeEvent,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvent.Kind == RuntimeEventKinds.BudgetUpdated
                && !string.IsNullOrWhiteSpace(
                    runtimeEvent.StreamAttemptId))
            {
                Volatile.Write(ref _usageCommitted, 1);
            }

            return result;
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            RejectIfUsageWasCommitted();
            return _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return _inner.ReadRunAsync(runId, cancellationToken);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetRunCursorAsync(runId, cancellationToken);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetOperationAsync(operationId, cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(runId, cancellationToken);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        private void RejectIfUsageWasCommitted()
        {
            if (Volatile.Read(ref _usageCommitted) == 0)
            {
                return;
            }

            RejectedPostUsageAppend = true;
            throw new IOException(
                "Simulated process loss after provider usage.");
        }
    }
}
