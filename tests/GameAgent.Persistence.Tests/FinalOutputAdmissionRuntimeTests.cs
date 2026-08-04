using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class FinalOutputAdmissionRuntimeTests
{
    private static readonly TimeSpan TestWaitTimeout =
        TimeSpan.FromSeconds(10);

    [Fact]
    public async Task BareTextIsProvisionalUntilStructuredOutputIsAdmitted()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var provider = new ScriptedProvider(
                FinalText("\"not-formal\""),
                ToolCall(
                    "submit-1",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"kind":"ok"},"evidence":[]}"""));
            var events = new RecordingPublisher();
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator(),
                events);
            await using var runtime = CreateRuntime(
                store,
                journal,
                provider,
                new RejectingHost(),
                policy);
            var contract = new FinalOutputContract(
                "test.output",
                "1",
                Json(
                    """
                    {
                      "type":"object",
                      "properties":{"kind":{"const":"ok"}},
                      "required":["kind"],
                      "additionalProperties":false
                    }
                    """));

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(),
                    FinalOutputContract = contract
                }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(
                outcome.ErrorCode is null,
                outcome.ErrorCode + ": " + outcome.SafeErrorMessage);
            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(
                "ok",
                outcome.FinalOutput!.Value
                    .GetProperty("kind")
                    .GetString());
            Assert.Equal(2, provider.Requests.Count);
            Assert.Single(policy.Requests);
            var submit = Assert.Single(
                provider.Requests[0].Tools,
                tool => string.Equals(
                    tool.Name,
                    FinalOutputAdmissionControl.SubmitToolName,
                    StringComparison.Ordinal));
            Assert.Equal(
                "object",
                submit.ParametersSchema
                    .GetProperty("properties")
                    .GetProperty("output")
                    .GetProperty("type")
                    .GetString());
            Assert.Contains(
                provider.Requests[1].Messages,
                message => message.Parts.Any(
                    part => part.Json.HasValue
                            && part.Json.Value.TryGetProperty(
                                "reasonCode",
                                out var reason)
                            && string.Equals(
                                reason.GetString(),
                                "final_output_submission_required",
                                StringComparison.Ordinal)));
            var deltas = events.Events.Where(
                item => string.Equals(
                    item.Kind,
                    RuntimeEventKinds.AssistantDelta,
                    StringComparison.Ordinal));
            Assert.NotEmpty(deltas);
            Assert.All(
                deltas,
                item => Assert.Equal(
                    "provisional",
                    item.Payload
                        .GetProperty("presentationState")
                        .GetString()));
            var providerResults = events.Events
                .Where(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.ProviderResultCommitted,
                        StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, providerResults.Length);
            Assert.Equal(
                FinalOutputAdmissionCodec.ProvisionalPresentationState,
                providerResults[0].Extensions[
                        FinalOutputAdmissionControl
                            .PresentationExtensionName]
                    .GetProperty("state")
                    .GetString());
            Assert.Equal(
                "final_output_submission_required",
                providerResults[0].Extensions[
                        FinalOutputAdmissionControl
                            .PresentationExtensionName]
                    .GetProperty("reasonCode")
                    .GetString());
            Assert.Equal(
                FinalOutputAdmissionCodec.AdmittedPresentationState,
                providerResults[1].Extensions[
                        FinalOutputAdmissionControl
                            .PresentationExtensionName]
                    .GetProperty("state")
                    .GetString());
            var completed = Assert.Single(
                events.Events,
                item => string.Equals(
                    item.Kind,
                    RuntimeEventKinds.AssistantCompleted,
                    StringComparison.Ordinal));
            Assert.True(
                completed.Extensions.ContainsKey(
                    FinalOutputAdmissionCodec.EvidenceExtensionName));
            Assert.Equal(
                FinalOutputAdmissionCodec.AdmittedPresentationState,
                completed.Extensions[
                        FinalOutputAdmissionControl
                            .PresentationExtensionName]
                    .GetProperty("state")
                    .GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PolicyRejectionReturnsTypedResultAndAllowsCorrection()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var provider = new ScriptedProvider(
                ToolCall(
                    "submit-1",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"score":0},"evidence":[]}"""),
                ToolCall(
                    "submit-2",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"score":2},"evidence":[]}"""));
            var policy = new RecordingPolicy(
                request => request.Proposal.Output
                                   .GetProperty("score")
                                   .GetInt32() >= 1
                    ? FinalOutputAdmissionDecision.Accept()
                    : FinalOutputAdmissionDecision.Reject(
                        "score_too_low",
                        Json("""{"minimum":1}""")));
            var events = new RecordingPublisher();
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator(),
                events);
            await using var runtime = CreateRuntime(
                store,
                journal,
                provider,
                new RejectingHost(),
                policy);

            var outcome = await runtime.RunAsync(
                new DurableRunRequest { Run = Run() }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Equal(2, policy.Requests.Count);
            var result = provider.Requests[1].Messages
                .SelectMany(message => message.Parts)
                .Single(
                    part => string.Equals(
                                part.Type,
                                NormalizedPartTypes.ToolResult,
                                StringComparison.Ordinal)
                            && string.Equals(
                                part.ToolCallId,
                                "submit-1",
                                StringComparison.Ordinal))
                .Json!.Value;
            Assert.False(result.GetProperty("admitted").GetBoolean());
            Assert.Equal(
                "score_too_low",
                result.GetProperty("reasonCode").GetString());
            Assert.Equal(
                1,
                result.GetProperty("feedback")
                    .GetProperty("minimum")
                    .GetInt32());
            var providerResults = events.Events
                .Where(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.ProviderResultCommitted,
                        StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, providerResults.Length);
            var rejectedPresentation = providerResults[0].Extensions[
                FinalOutputAdmissionControl.PresentationExtensionName];
            Assert.Equal(
                FinalOutputAdmissionCodec.ProvisionalPresentationState,
                rejectedPresentation.GetProperty("state").GetString());
            Assert.Equal(
                "score_too_low",
                rejectedPresentation
                    .GetProperty("reasonCode")
                    .GetString());
            Assert.Single(
                events.Events,
                item => string.Equals(
                    item.Kind,
                    RuntimeEventKinds.AssistantCompleted,
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MixedAndMultipleSubmissionsNeverReachTheHost()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var provider = new ScriptedProvider(
                ToolCalls(
                    (
                        "submit-1",
                        FinalOutputAdmissionControl.SubmitToolName,
                        """{"output":{"ok":true},"evidence":[]}"""),
                    (
                        "submit-2",
                        FinalOutputAdmissionControl.SubmitToolName,
                        """{"output":{"ok":true},"evidence":[]}"""),
                    (
                        "game-call",
                        "set_flag",
                        """{"entityId":"npc-1"}""")),
                ToolCall(
                    "submit-valid",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true},"evidence":[]}"""));
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var runtime = CreateRuntime(
                store,
                journal,
                provider,
                new RejectingHost(),
                policy,
                Tool("set_flag"));

            var completed = await runtime.RunAsync(
                new DurableRunRequest { Run = Run() }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, completed.Run.State);
            Assert.Single(policy.Requests);
            var results = provider.Requests[1].Messages
                .SelectMany(message => message.Parts)
                .Where(
                    part => string.Equals(
                        part.Type,
                        NormalizedPartTypes.ToolResult,
                        StringComparison.Ordinal))
                .ToDictionary(
                    part => part.ToolCallId!,
                    part => part.Json!.Value,
                    StringComparer.Ordinal);
            Assert.Equal(3, results.Count);
            Assert.Equal(
                "final_output_submission_must_be_exclusive",
                results["submit-1"]
                    .GetProperty("reasonCode")
                    .GetString());
            Assert.Equal(
                "final_output_submission_must_be_exclusive",
                results["submit-2"]
                    .GetProperty("reasonCode")
                    .GetString());
            Assert.Equal(
                "final_output_submission_must_be_exclusive",
                results["game-call"]
                    .GetProperty("reasonCode")
                    .GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MemoryReceivesOnlyTheAdmittedAssistantOutput()
    {
        var directory = TempDirectory();
        var memory = new RuntimeMemoryLifecycle(
            Array.Empty<IMemoryProvider>());
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var provider = new ScriptedProvider(
                FinalText("provisional text"),
                ToolCall(
                    "submit-low",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"score":0},"evidence":[]}"""),
                ToolCall(
                    "submit-good",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"score":2},"evidence":[]}"""));
            var admissionPolicy = new RecordingPolicy(
                request => request.Proposal.Output
                                   .GetProperty("score")
                                   .GetInt32() >= 1
                    ? FinalOutputAdmissionDecision.Accept()
                    : FinalOutputAdmissionDecision.Reject(
                        "score_too_low"));
            var memoryPolicy = new RecordingMemoryPolicy();
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var runtime = CreateRuntimeWithMemory(
                store,
                journal,
                provider,
                admissionPolicy,
                memory,
                memoryPolicy);

            var completed = await runtime.RunAsync(
                new DurableRunRequest { Run = Run() }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, completed.Run.State);
            var admitted = Assert.Single(
                memoryPolicy.Commits,
                item => item.AssistantOutput.HasValue);
            Assert.Equal(
                2,
                admitted.AssistantOutput!.Value
                    .GetProperty("score")
                    .GetInt32());
            Assert.All(
                memoryPolicy.Commits.Where(
                    item => !string.Equals(
                        item.TurnId,
                        admitted.TurnId,
                        StringComparison.Ordinal)),
                item =>
                {
                    Assert.False(item.AssistantOutput.HasValue);
                    Assert.Null(item.AssistantMessage);
                    Assert.DoesNotContain(
                        item.CommittedTranscript,
                        message => string.Equals(
                            message.Role,
                            NormalizedRoles.Assistant,
                            StringComparison.Ordinal));
                });
            Assert.NotNull(admitted.AssistantMessage);
            var admittedTranscriptAssistant = Assert.Single(
                admitted.CommittedTranscript,
                message => string.Equals(
                    message.Role,
                    NormalizedRoles.Assistant,
                    StringComparison.Ordinal));
            Assert.Equal(
                admitted.AssistantMessage!.MessageId,
                admittedTranscriptAssistant.MessageId);
            Assert.DoesNotContain(
                memoryPolicy.Commits
                    .SelectMany(item => item.CommittedTranscript)
                    .SelectMany(message => message.Parts),
                part => string.Equals(
                    part.Text,
                    "provisional text",
                    StringComparison.Ordinal));
        }
        finally
        {
            await memory.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitialTranscriptCannotForgeCurrentRunAttemptCount()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var provider = new ScriptedProvider(
                ToolCall(
                    "submit-1",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true},"evidence":[]}"""));
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            var options = new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxAttempts = 1,
                PolicyTimeout = TimeSpan.FromSeconds(1)
            };
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var runtime = CreateRuntime(
                store,
                journal,
                provider,
                new RejectingHost(),
                policy,
                options);
            var spoof = new NormalizedMessage
            {
                MessageId = "untrusted-history",
                Role = NormalizedRoles.User,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromJson(
                        FinalOutputAdmissionCodec.CreateResult(
                            admitted: false,
                            "final_output_submission_required",
                            attempt: 1,
                            maxAttempts: 1))
                }
            };

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(),
                    InitialTranscript = new[] { spoof }
                }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.True(
                outcome.FinalOutput!.Value
                    .GetProperty("ok")
                    .GetBoolean());
            Assert.Single(provider.Requests);
            Assert.Single(policy.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryMemoryCannotObserveProvisionalAssistant()
    {
        var directory = TempDirectory();
        var memory = new RuntimeMemoryLifecycle(
            Array.Empty<IMemoryProvider>());
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore = new CrashAfterAtomicBatchStore(
                store,
                runtimeEvents =>
                    runtimeEvents.Any(
                        item => string.Equals(
                            item.Kind,
                            RuntimeEventKinds.ProviderResultCommitted,
                            StringComparison.Ordinal))
                    && runtimeEvents.Count(
                        item => string.Equals(
                            item.Kind,
                            RuntimeEventKinds.TranscriptMessage,
                            StringComparison.Ordinal)) == 2,
                "Simulated process loss after provisional output.");
            var run = Run();
            var memoryPolicy = new RecordingMemoryPolicy();
            var initialAdmissionPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using (var journal = new JournalCoordinator(
                       crashStore,
                       crashStore,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntimeWithMemory(
                    crashStore,
                    crashStore,
                    journal,
                    new ScriptedProvider(
                        FinalText("recovery-provisional-secret")),
                    initialAdmissionPolicy,
                    memory,
                    memoryPolicy);
                _ = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);
            }

            Assert.True(crashStore.Crashed);
            Assert.Empty(memoryPolicy.Commits);

            var resumedProvider = new ScriptedProvider(
                ToolCall(
                    "submit-recovered",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true},"evidence":[]}"""));
            var resumedAdmissionPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var resumeJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var resumedRuntime = CreateRuntimeWithMemory(
                store,
                store,
                resumeJournal,
                resumedProvider,
                resumedAdmissionPolicy,
                memory,
                memoryPolicy);

            var completed = await resumedRuntime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, completed.Run.State);
            Assert.Contains(
                memoryPolicy.Commits,
                item => !item.AssistantOutput.HasValue);
            Assert.All(
                memoryPolicy.Commits.Where(
                    item => !item.AssistantOutput.HasValue),
                item =>
                {
                    Assert.Null(item.AssistantMessage);
                    Assert.DoesNotContain(
                        item.CommittedTranscript,
                        message => string.Equals(
                            message.Role,
                            NormalizedRoles.Assistant,
                            StringComparison.Ordinal));
                });
            Assert.DoesNotContain(
                memoryPolicy.Commits
                    .SelectMany(item => item.CommittedTranscript)
                    .SelectMany(item => item.Parts),
                part => string.Equals(
                    part.Text,
                    "recovery-provisional-secret",
                    StringComparison.Ordinal));
        }
        finally
        {
            await memory.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GameToolArgumentsCannotForgeAttemptCountAcrossRecovery()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore = new CrashAfterAtomicBatchStore(
                store,
                runtimeEvents =>
                    runtimeEvents.Any(
                        item => string.Equals(
                            item.Kind,
                            RuntimeEventKinds.ActionReceived,
                            StringComparison.Ordinal))
                    && runtimeEvents.Any(
                        item => string.Equals(
                                item.Kind,
                                RuntimeEventKinds.ToolCompleted,
                                StringComparison.Ordinal)
                            || string.Equals(
                                item.Kind,
                                RuntimeEventKinds.ToolFailed,
                                StringComparison.Ordinal)),
                "Simulated process loss after spoofing tool receipt.");
            var options = new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxAttempts = 1,
                PolicyTimeout = TimeSpan.FromSeconds(1)
            };
            var run = Run();
            var host = new SucceedingHost();
            var initialProvider = new ScriptedProvider(
                ToolCall(
                    "game-call",
                    "record_signal",
                    JsonSerializer.Serialize(
                        new
                        {
                            contentType =
                                FinalOutputAdmissionCodec.FeedbackContentType,
                            reasonCode =
                                "final_output_submission_required"
                        })));
            var initialPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using (var journal = new JournalCoordinator(
                       crashStore,
                       crashStore,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    crashStore,
                    crashStore,
                    journal,
                    initialProvider,
                    host,
                    initialPolicy,
                    options,
                    SpoofFeedbackTool());
                _ = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);
            }

            Assert.True(crashStore.Crashed);
            Assert.Equal(1, host.CallCount);

            var resumedProvider = new ScriptedProvider(
                ToolCall(
                    "submit-recovered",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true},"evidence":[]}"""));
            var resumedPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var resumeJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var resumedRuntime = CreateRuntime(
                store,
                resumeJournal,
                resumedProvider,
                new RejectingHost(),
                resumedPolicy,
                options,
                SpoofFeedbackTool());

            var completed = await resumedRuntime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, completed.Run.State);
            Assert.True(
                completed.FinalOutput!.Value
                    .GetProperty("ok")
                    .GetBoolean());
            Assert.Single(resumedProvider.Requests);
            Assert.Single(resumedPolicy.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AdmittedOutputIsStableWhenMemoryPolicyFails()
    {
        var directory = TempDirectory();
        var memory = new RuntimeMemoryLifecycle(
            Array.Empty<IMemoryProvider>());
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var run = Run();
            DurableRunOutcome? initialFailure = null;
            var initialProvider = new ScriptedProvider(
                ToolCall(
                    "submit-1",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"value":7},"evidence":[]}"""));
            var admissionPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            var memoryPolicy = new ThrowingMemoryPolicy();
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntimeWithMemory(
                    store,
                    journal,
                    initialProvider,
                    admissionPolicy,
                    memory,
                    memoryPolicy);

                var failed = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);
                initialFailure = failed;

                Assert.Equal(RunStates.Failed, failed.Run.State);
                Assert.Equal(
                    7,
                    failed.FinalOutput!.Value
                        .GetProperty("value")
                        .GetInt32());
                Assert.Equal(1, memoryPolicy.CallCount);
            }

            var terminalEvent = Assert.Single(
                await store.ReadRunAsync(
                    run.RunId,
                    CancellationToken.None),
                item => item.Extensions.ContainsKey(
                    TerminalOutcomeJournalCodec.ExtensionName));
            var durableFailure = TerminalOutcomeJournalCodec.Read(
                terminalEvent.Extensions[
                    TerminalOutcomeJournalCodec.ExtensionName]);
            Assert.Equal(initialFailure!.ErrorCode, durableFailure.Code);
            Assert.Equal(
                initialFailure.ErrorCategory,
                durableFailure.Category);
            Assert.Equal(
                initialFailure.SafeErrorMessage,
                durableFailure.SafeMessage);

            var replayProvider = new ScriptedProvider();
            var replayAdmissionPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var replayJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var replayRuntime = CreateRuntimeWithMemory(
                store,
                replayJournal,
                replayProvider,
                replayAdmissionPolicy,
                memory,
                memoryPolicy);

            var replay = await replayRuntime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Failed, replay.Run.State);
            Assert.Equal(
                7,
                replay.FinalOutput!.Value
                    .GetProperty("value")
                    .GetInt32());
            Assert.Equal(initialFailure.ErrorCode, replay.ErrorCode);
            Assert.Equal(
                initialFailure.ErrorCategory,
                replay.ErrorCategory);
            Assert.Equal(
                initialFailure.SafeErrorMessage,
                replay.SafeErrorMessage);
            Assert.Empty(replayProvider.Requests);
            Assert.Empty(replayAdmissionPolicy.Requests);
            Assert.Equal(1, memoryPolicy.CallCount);
        }
        finally
        {
            await memory.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OutputSchemaFailsClosedBeforePolicyAndStringRemainsValid()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var provider = new ScriptedProvider(
                ToolCall(
                    "submit-bad",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":"wrong","evidence":[]}"""),
                ToolCall(
                    "submit-good",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"value":7},"evidence":[]}"""));
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var runtime = CreateRuntime(
                store,
                journal,
                provider,
                new RejectingHost(),
                policy);
            var contract = new FinalOutputContract(
                "structured",
                "1",
                Json(
                    """
                    {
                      "type":"object",
                      "properties":{"value":{"type":"integer"}},
                      "required":["value"],
                      "additionalProperties":false
                    }
                    """));

            var outcome = await runtime.RunAsync(
                new DurableRunRequest
                {
                    Run = Run(),
                    FinalOutputContract = contract
                }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, outcome.Run.State);
            Assert.Single(policy.Requests);
            Assert.Equal(
                7,
                outcome.FinalOutput!.Value
                    .GetProperty("value")
                    .GetInt32());

            var stringProvider = new ScriptedProvider(
                ToolCall(
                    "submit-string",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":"plain string","evidence":[]}"""));
            var stringRun = Run();
            await using var stringRuntime = CreateRuntime(
                store,
                journal,
                stringProvider,
                new RejectingHost(),
                policy);
            var stringOutcome = await stringRuntime.RunAsync(
                new DurableRunRequest { Run = stringRun }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(
                "plain string",
                stringOutcome.FinalOutput!.Value.GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ForgedEvidenceIsRejectedAndAcceptedRunReplaysWithoutHostWork()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var host = new SucceedingHost();
            var provider = new ScriptedProvider(
                ToolCall(
                    "game-call",
                    "set_flag",
                    """{"entityId":"npc-1"}"""),
                request => EvidenceSubmission(
                    request,
                    "submit-forged",
                    forgeSource: true),
                request => EvidenceSubmission(
                    request,
                    "submit-valid",
                    forgeSource: false));
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            var ids = new GuidRuntimeIdGenerator();
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       ids))
            {
                await using var runtime = CreateRuntime(
                    store,
                    journal,
                    provider,
                    host,
                    policy,
                    Tool("set_flag"));
                var run = Run();
                var outcome = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal(RunStates.Completed, outcome.Run.State);
                Assert.Equal(1, host.CallCount);
                Assert.Single(policy.Requests);
                Assert.Single(policy.Requests[0].CommittedEvidence);
                Assert.Single(policy.Requests[0].Proposal.Evidence);
                Assert.Equal(
                    policy.Requests[0].CommittedEvidence[0].SourceEventId,
                    policy.Requests[0].Proposal.Evidence[0].SourceEventId);
            }

            var replayProvider = new ScriptedProvider();
            using var replayJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var replayRuntime = CreateRuntime(
                store,
                replayJournal,
                replayProvider,
                host,
                policy,
                Tool("set_flag"));
            var replay = await replayRuntime.ResumeAsync(
                provider.Requests[0].RunId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, replay.Run.State);
            Assert.Empty(replayProvider.Requests);
            Assert.Equal(1, host.CallCount);
            Assert.True(
                replay.FinalOutput!.Value
                    .GetProperty("committed")
                    .GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoverySynthesizesExactEvidenceAfterReceiptCommitCrash()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore = new CrashAfterAtomicBatchStore(
                store,
                runtimeEvents =>
                    runtimeEvents.Any(
                        item => string.Equals(
                            item.Kind,
                            RuntimeEventKinds.ActionReceived,
                            StringComparison.Ordinal))
                    && runtimeEvents.Any(
                        item => string.Equals(
                                item.Kind,
                                RuntimeEventKinds.ToolCompleted,
                                StringComparison.Ordinal)
                            || string.Equals(
                                item.Kind,
                                RuntimeEventKinds.ToolFailed,
                                StringComparison.Ordinal)),
                "Simulated process loss after terminal receipt commit.");
            var host = new SucceedingHost();
            var run = Run();
            var initialProvider = new ScriptedProvider(
                ToolCall(
                    "game-call",
                    "set_flag",
                    """{"entityId":"npc-1"}"""));
            var initialPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using (var journal = new JournalCoordinator(
                       crashStore,
                       crashStore,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    crashStore,
                    crashStore,
                    journal,
                    initialProvider,
                    host,
                    initialPolicy,
                    new FinalOutputAdmissionOptions
                    {
                        Enabled = true,
                        MaxAttempts = 4,
                        PolicyTimeout = TimeSpan.FromSeconds(1)
                    },
                    Tool("set_flag"));
                _ = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);
            }

            Assert.True(crashStore.Crashed);
            Assert.Equal(1, host.CallCount);
            var receiptEvent = Assert.Single(
                await store.ReadRunAsync(
                    run.RunId,
                    CancellationToken.None),
                item => string.Equals(
                    item.Kind,
                    RuntimeEventKinds.ActionReceived,
                    StringComparison.Ordinal));

            var resumedProvider = new ScriptedProvider(
                request => EvidenceSubmission(
                    request,
                    "submit-recovered",
                    forgeSource: false));
            var resumedPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var resumeJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var resumedRuntime = CreateRuntime(
                store,
                resumeJournal,
                resumedProvider,
                new RejectingHost(),
                resumedPolicy,
                Tool("set_flag"));

            var completed = await resumedRuntime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, completed.Run.State);
            Assert.Equal(1, host.CallCount);
            Assert.Single(resumedProvider.Requests);
            var policyRequest = Assert.Single(resumedPolicy.Requests);
            var cited = Assert.Single(policyRequest.Proposal.Evidence);
            Assert.Equal(receiptEvent.EventId, cited.SourceEventId);
            Assert.Equal(
                receiptEvent.EventId,
                Assert.Single(policyRequest.CommittedEvidence)
                    .SourceEventId);
            var recoveredResult = resumedProvider.Requests[0].Messages
                .SelectMany(message => message.Parts)
                .Single(
                    part => string.Equals(
                                part.Type,
                                NormalizedPartTypes.ToolResult,
                                StringComparison.Ordinal)
                            && string.Equals(
                                part.ToolName,
                                "set_flag",
                                StringComparison.Ordinal))
                .Json!.Value;
            Assert.Equal(
                FinalOutputAdmissionControl
                    .EvidencePresentationContentType,
                recoveredResult
                    .GetProperty("contentType")
                    .GetString());
            Assert.Equal(
                receiptEvent.EventId,
                recoveredResult
                    .GetProperty("evidenceReference")
                    .GetProperty(
                        FinalOutputAdmissionControl
                            .EvidenceSourceEventIdPropertyName)
                    .GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReceiptEvidenceWrapperPreservesAFullHostExtensionBag()
    {
        var request = Action("operation-1");
        var receipt = TerminalReceipt(
            request,
            ReceiptStatuses.Succeeded,
            revision: 0);
        for (var index = 0;
             index < ProtocolLimits.MaxProtocolExtensions;
             index++)
        {
            receipt.Extensions.Add(
                "host-" + index,
                JsonArrayBuilder.Number(index));
        }

        var admitted = ActionReceiptIngressValidator.ValidateAndClone(
            request,
            receipt);
        var presentation =
            FinalOutputAdmissionCodec.ForModelPresentation(
                admitted,
                "action-receipt:operation-1:0");

        Assert.Equal(
            ProtocolLimits.MaxProtocolExtensions,
            presentation.GetProperty("receipt")
                .GetProperty("extensions")
                .EnumerateObject()
                .Count());
        Assert.Equal(
            "action-receipt:operation-1:0",
            presentation.GetProperty("evidenceReference")
                .GetProperty(
                    FinalOutputAdmissionControl
                        .EvidenceSourceEventIdPropertyName)
                .GetString());
        Assert.False(
            admitted.Extensions.ContainsKey(
                FinalOutputAdmissionControl
                    .EvidenceSourceEventIdPropertyName));
    }

    [Fact]
    public void HostCannotPrepopulateRuntimeEvidenceMetadata()
    {
        var request = Action("operation-1");
        var receipt = TerminalReceipt(
            request,
            ReceiptStatuses.Succeeded,
            revision: 0);
        receipt.Extensions[
                FinalOutputAdmissionControl
                    .EvidenceSourceEventIdPropertyName] =
            JsonArrayBuilder.String("forged-source");

        var error = Assert.Throws<InvalidDataException>(
            () => ActionReceiptIngressValidator.ValidateAndClone(
                request,
                receipt));

        Assert.Contains(
            "runtime-reserved",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceMustBeTerminalAndBelongToTheCurrentRun()
    {
        var request = Action("operation-1");
        var unknown = TerminalReceipt(
            request,
            ReceiptStatuses.Unknown,
            revision: 0);
        unknown.CommittedAt = null;
        Assert.Throws<ArgumentException>(
            () => new FinalOutputCommittedEvidence(
                "run-a",
                "turn-1",
                "receipt-event-1",
                unknown));

        var receipt = TerminalReceipt(
            request,
            ReceiptStatuses.Succeeded,
            revision: 1);
        var registry = new FinalOutputEvidenceRegistry();
        registry.Add(
            new FinalOutputCommittedEvidence(
                "run-a",
                "turn-1",
                "receipt-event-1",
                receipt));
        var call = new ModelToolCall
        {
            ToolCallId = "submit-1",
            Name = FinalOutputAdmissionControl.SubmitToolName,
            Arguments = Json(
                """
                {
                  "output":{"ok":true},
                  "evidence":[{
                    "operationId":"operation-1",
                    "revision":1,
                    "sourceEventId":"receipt-event-1"
                  }]
                }
                """)
        };

        Assert.False(
            FinalOutputAdmissionCodec.TryParseSubmission(
                call,
                new FinalOutputAdmissionOptions { Enabled = true },
                contract: null,
                registry,
                "run-b",
                out _,
                out var crossRunReason));
        Assert.Equal(
            "final_output_evidence_not_committed",
            crossRunReason);
        Assert.True(
            FinalOutputAdmissionCodec.TryParseSubmission(
                call,
                new FinalOutputAdmissionOptions { Enabled = true },
                contract: null,
                registry,
                "run-a",
                out var currentRun,
                out _));
        Assert.Single(currentRun!.Evidence);
    }

    [Fact]
    public async Task UnknownReceiptBecomesCitableOnlyAfterReconciliation()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            var firstProvider = new ScriptedProvider(
                ToolCall(
                    "game-call",
                    "set_flag",
                    """{"entityId":"npc-1"}"""));
            var run = Run();
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    store,
                    journal,
                    firstProvider,
                    new UnknownHost(),
                    policy,
                    Tool("set_flag"));
                var unresolved = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal(
                    RunStates.Reconciling,
                    unresolved.Run.State);
                Assert.Empty(policy.Requests);
                Assert.DoesNotContain(
                    unresolved.Transcript.SelectMany(
                        message => message.Parts),
                    part => string.Equals(
                        part.ToolName,
                        "set_flag",
                        StringComparison.Ordinal)
                            && string.Equals(
                                part.Type,
                                NormalizedPartTypes.ToolResult,
                                StringComparison.Ordinal));
            }

            var reconciler = new TerminalReconciler();
            var resumedProvider = new ScriptedProvider(
                request => EvidenceSubmission(
                    request,
                    "submit-reconciled",
                    forgeSource: false));
            using var resumeJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var resumedRuntime = CreateRuntime(
                store,
                resumeJournal,
                resumedProvider,
                new RejectingHost(),
                policy,
                Tool("set_flag"));

            var completed = await resumedRuntime.ResumeAsync(
                run.RunId,
                reconciler: reconciler, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Completed, completed.Run.State);
            Assert.Equal(1, reconciler.CallCount);
            Assert.Single(policy.Requests);
            Assert.Single(policy.Requests[0].Proposal.Evidence);
            Assert.Equal(
                1,
                policy.Requests[0].Proposal.Evidence[0]
                    .Receipt.Revision);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AttemptLimitFailsDurablyAndDoesNotReenterProvider()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var provider = new ScriptedProvider(
                ToolCall(
                    "submit-1",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true}}"""),
                ToolCall(
                    "submit-2",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true}}"""),
                ToolCall(
                    "submit-3",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true}}"""),
                ToolCall(
                    "submit-4",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true}}"""));
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            var run = Run();
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    store,
                    journal,
                    provider,
                    new RejectingHost(),
                    policy);
                var failed = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal(RunStates.Failed, failed.Run.State);
                Assert.Equal(
                    "final_output_admission_attempts_exhausted",
                    failed.ErrorCode);
                Assert.Equal(
                    "final_output_admission_attempts_exhausted",
                    failed.Run.TerminalReason);
                Assert.Empty(policy.Requests);
                Assert.Equal(4, provider.Requests.Count);
            }

            var replayProvider = new ScriptedProvider();
            using var replayJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var replayRuntime = CreateRuntime(
                store,
                replayJournal,
                replayProvider,
                new RejectingHost(),
                policy);
            var replay = await replayRuntime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(RunStates.Failed, replay.Run.State);
            Assert.Empty(replayProvider.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeFailsBeforeDispatchWhenLastRejectionSurvivedCrash()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore =
                new CrashAfterTurnCompletedStore(store);
            var run = Run();
            var options = new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxAttempts = 1,
                PolicyTimeout = TimeSpan.FromSeconds(1)
            };
            var initialProvider = new ScriptedProvider(
                ToolCall(
                    "submit-invalid",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true}}"""));
            var initialPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using (var journal = new JournalCoordinator(
                       crashStore,
                       crashStore,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    crashStore,
                    crashStore,
                    journal,
                    initialProvider,
                    new RejectingHost(),
                    initialPolicy,
                    options);

                _ = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(crashStore.Crashed);
                Assert.Single(initialProvider.Requests);
                Assert.Empty(initialPolicy.Requests);
            }

            var resumedProvider = new ScriptedProvider(
                ToolCall(
                    "submit-should-not-run",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true},"evidence":[]}"""));
            var resumedPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    store,
                    journal,
                    resumedProvider,
                    new RejectingHost(),
                    resumedPolicy,
                    options);

                var failed = await runtime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal(RunStates.Failed, failed.Run.State);
                Assert.Equal(
                    "final_output_admission_attempts_exhausted",
                    failed.ErrorCode);
                Assert.Empty(resumedProvider.Requests);
                Assert.Empty(resumedPolicy.Requests);
            }

            var replayProvider = new ScriptedProvider();
            var replayPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var replayJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var replayRuntime = CreateRuntime(
                store,
                replayJournal,
                replayProvider,
                new RejectingHost(),
                replayPolicy,
                options);
            var replay = await replayRuntime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(RunStates.Failed, replay.Run.State);
            Assert.Equal(
                "final_output_admission_attempts_exhausted",
                replay.Run.TerminalReason);
            Assert.Empty(replayProvider.Requests);
            Assert.Empty(replayPolicy.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingSubmissionFeedbackAndAttemptAreAtomicAcrossCrash()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var crashStore = new CrashAfterAtomicBatchStore(
                store,
                runtimeEvents =>
                    runtimeEvents.Any(
                        item => string.Equals(
                            item.Kind,
                            RuntimeEventKinds.ProviderResultCommitted,
                            StringComparison.Ordinal))
                    && runtimeEvents.Count(
                        item => string.Equals(
                            item.Kind,
                            RuntimeEventKinds.TranscriptMessage,
                            StringComparison.Ordinal)) == 2,
                "Simulated process loss after missing-submission batch.");
            var run = Run();
            var options = new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxAttempts = 1,
                PolicyTimeout = TimeSpan.FromSeconds(1)
            };
            var initialProvider = new ScriptedProvider(
                FinalText("missing structured submission"));
            var initialPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using (var journal = new JournalCoordinator(
                       crashStore,
                       crashStore,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    crashStore,
                    crashStore,
                    journal,
                    initialProvider,
                    new RejectingHost(),
                    initialPolicy,
                    options);
                _ = await runtime.RunAsync(
                    new DurableRunRequest { Run = run }, cancellationToken: TestContext.Current.CancellationToken);
            }

            Assert.True(crashStore.Crashed);
            Assert.Single(initialProvider.Requests);
            var durableEvents = await store.ReadRunAsync(
                run.RunId,
                CancellationToken.None);
            var durableMessages = durableEvents
                .Where(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.TranscriptMessage,
                        StringComparison.Ordinal))
                .Select(
                    item => NormalizedMessageJournalCodec.Decode(
                        item.Payload))
                .ToArray();
            Assert.Contains(
                durableMessages,
                message => message.Parts.Any(
                    part => part.Json.HasValue
                            && part.Json.Value.TryGetProperty(
                                "reasonCode",
                                out var reason)
                            && string.Equals(
                                reason.GetString(),
                                "final_output_submission_required",
                                StringComparison.Ordinal)));

            var resumedProvider = new ScriptedProvider(
                ToolCall(
                    "submit-should-not-run",
                    FinalOutputAdmissionControl.SubmitToolName,
                    """{"output":{"ok":true},"evidence":[]}"""));
            var resumedPolicy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            using var resumeJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var resumedRuntime = CreateRuntime(
                store,
                resumeJournal,
                resumedProvider,
                new RejectingHost(),
                resumedPolicy,
                options);

            var failed = await resumedRuntime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Failed, failed.Run.State);
            Assert.Equal(
                "final_output_admission_attempts_exhausted",
                failed.Run.TerminalReason);
            Assert.Empty(resumedProvider.Requests);
            Assert.Empty(resumedPolicy.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeRejectsPolicyIdentityAndContractMismatch()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var contract = new FinalOutputContract(
                "result",
                "1",
                Json("""{"type":"object"}"""));
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            var run = Run();
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    store,
                    journal,
                    new ScriptedProvider(
                        ToolCall(
                            "game-call",
                            "set_flag",
                            """{"entityId":"npc-1"}""")),
                    new UnknownHost(),
                    policy,
                    Tool("set_flag"));
                var unresolved = await runtime.RunAsync(
                    new DurableRunRequest
                    {
                        Run = run,
                        FinalOutputContract = contract
                    }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(
                    RunStates.Reconciling,
                    unresolved.Run.State);
            }

            using (var changedJournal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var changedRuntime = CreateRuntime(
                    store,
                    changedJournal,
                    new ScriptedProvider(),
                    new RejectingHost(),
                    new RecordingPolicy(
                        _ => FinalOutputAdmissionDecision.Accept(),
                        version: "2"),
                    Tool("set_flag"));
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => changedRuntime.ResumeAsync(run.RunId, cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());
            }

            using var correctJournal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var correctRuntime = CreateRuntime(
                store,
                correctJournal,
                new ScriptedProvider(),
                new RejectingHost(),
                policy,
                Tool("set_flag"));
            var wrongContract = new FinalOutputContract(
                "result",
                "2",
                Json("""{"type":"string"}"""));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => correctRuntime.ResumeAsync(
                        run.RunId,
                        new DurableRunContinuation
                        {
                            FinalOutputContract = wrongContract
                        }, cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeRejectsStrictModeAndOptionsChanges()
    {
        var directory = TempDirectory();
        try
        {
            var policy = new RecordingPolicy(
                _ => FinalOutputAdmissionDecision.Accept());
            var strictOptions = new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxAttempts = 4,
                PolicyTimeout = TimeSpan.FromSeconds(1)
            };

            await using (var strictStore = new FileSessionStore(
                             Path.Combine(
                                 directory,
                                 "strict.journal")))
            {
                var strictRun = Run();
                using (var journal = new JournalCoordinator(
                           strictStore,
                           strictStore,
                           new SystemRuntimeClock(),
                           new GuidRuntimeIdGenerator()))
                {
                    await using var runtime = CreateRuntime(
                        strictStore,
                        journal,
                        new ScriptedProvider(
                            ToolCall(
                                "strict-game-call",
                                "set_flag",
                                """{"entityId":"npc-1"}""")),
                        new UnknownHost(),
                        policy,
                        strictOptions,
                        Tool("set_flag"));
                    var pending = await runtime.RunAsync(
                        new DurableRunRequest { Run = strictRun }, cancellationToken: TestContext.Current.CancellationToken);
                    Assert.Equal(
                        RunStates.Reconciling,
                        pending.Run.State);
                }

                using (var journal = new JournalCoordinator(
                           strictStore,
                           strictStore,
                           new SystemRuntimeClock(),
                           new GuidRuntimeIdGenerator()))
                {
                    await using var nonStrict =
                        CreateNonStrictRuntime(
                            strictStore,
                            journal,
                            new ScriptedProvider());
                    await Assert.ThrowsAsync<InvalidDataException>(
                        () => nonStrict.ResumeAsync(strictRun.RunId, cancellationToken: TestContext.Current.CancellationToken)
                            .AsTask());
                }

                using var changedJournal = new JournalCoordinator(
                    strictStore,
                    strictStore,
                    new SystemRuntimeClock(),
                    new GuidRuntimeIdGenerator());
                await using var changedOptionsRuntime = CreateRuntime(
                    strictStore,
                    changedJournal,
                    new ScriptedProvider(),
                    new RejectingHost(),
                    policy,
                    new FinalOutputAdmissionOptions
                    {
                        Enabled = true,
                        MaxAttempts = 3,
                        PolicyTimeout = TimeSpan.FromSeconds(1)
                    },
                    Tool("set_flag"));
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => changedOptionsRuntime
                        .ResumeAsync(strictRun.RunId, cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());
            }

            await using (var nonStrictStore = new FileSessionStore(
                             Path.Combine(
                                 directory,
                                 "non-strict.journal")))
            {
                var nonStrictRun = Run();
                using (var journal = new JournalCoordinator(
                           nonStrictStore,
                           nonStrictStore,
                           new SystemRuntimeClock(),
                           new GuidRuntimeIdGenerator()))
                {
                    await using var runtime = CreateNonStrictRuntime(
                        nonStrictStore,
                        journal,
                        new ScriptedProvider(
                            ToolCall(
                                "non-strict-game-call",
                                "set_flag",
                                """{"entityId":"npc-1"}""")),
                        new UnknownHost(),
                        Tool("set_flag"));
                    var pending = await runtime.RunAsync(
                        new DurableRunRequest { Run = nonStrictRun }, cancellationToken: TestContext.Current.CancellationToken);
                    Assert.Equal(
                        RunStates.Reconciling,
                        pending.Run.State);
                }

                using var strictJournal = new JournalCoordinator(
                    nonStrictStore,
                    nonStrictStore,
                    new SystemRuntimeClock(),
                    new GuidRuntimeIdGenerator());
                await using var strictRuntime = CreateRuntime(
                    nonStrictStore,
                    strictJournal,
                    new ScriptedProvider(),
                    new RejectingHost(),
                    policy,
                    strictOptions,
                    Tool("set_flag"));
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => strictRuntime
                        .ResumeAsync(nonStrictRun.RunId, cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryRejectsMissingTamperedAndExtraPresentationMetadata()
    {
        var directory = TempDirectory();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var strictRun = Run();
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateRuntime(
                    store,
                    journal,
                    new ScriptedProvider(
                        FinalText("provisional"),
                        ToolCall(
                            "submit-1",
                            FinalOutputAdmissionControl.SubmitToolName,
                            """{"output":{"ok":true},"evidence":[]}""")),
                    new RejectingHost(),
                    new RecordingPolicy(
                        _ => FinalOutputAdmissionDecision.Accept()));
                var completed = await runtime.RunAsync(
                    new DurableRunRequest { Run = strictRun }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(RunStates.Completed, completed.Run.State);
            }

            var strictEvents = await store.ReadRunAsync(
                strictRun.RunId,
                CancellationToken.None);
            var strictCursor = await store.GetRunCursorAsync(
                strictRun.RunId, cancellationToken: TestContext.Current.CancellationToken);

            var missing = CloneEvents(strictEvents);
            missing.First(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.ProviderResultCommitted,
                        StringComparison.Ordinal))
                .Extensions.Remove(
                    FinalOutputAdmissionControl
                        .PresentationExtensionName);
            await AssertRecoveryFailsAsync(
                strictRun.RunId,
                missing,
                strictCursor);

            var missingCompletion = CloneEvents(strictEvents);
            missingCompletion.Single(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.AssistantCompleted,
                        StringComparison.Ordinal))
                .Extensions.Remove(
                    FinalOutputAdmissionControl
                        .PresentationExtensionName);
            await AssertRecoveryFailsAsync(
                strictRun.RunId,
                missingCompletion,
                strictCursor);

            var tampered = CloneEvents(strictEvents);
            var completedEvent = tampered.Single(
                item => string.Equals(
                    item.Kind,
                    RuntimeEventKinds.AssistantCompleted,
                    StringComparison.Ordinal));
            var admittedPresentation = completedEvent.Extensions[
                FinalOutputAdmissionControl.PresentationExtensionName];
            completedEvent.Extensions[
                    FinalOutputAdmissionControl
                        .PresentationExtensionName] =
                JsonArrayBuilder.Object(
                    ("contentType", JsonArrayBuilder.String(
                        FinalOutputAdmissionControl
                            .PresentationContentType)),
                    ("state", JsonArrayBuilder.String(
                        FinalOutputAdmissionCodec
                            .AdmittedPresentationState)),
                    ("reasonCode", JsonArrayBuilder.String(
                        admittedPresentation
                            .GetProperty("reasonCode")
                            .GetString()!)),
                    ("evidenceDigest", JsonArrayBuilder.String(
                        new string('0', 64))));
            await AssertRecoveryFailsAsync(
                strictRun.RunId,
                tampered,
                strictCursor);

            var extraAdmitted = CloneEvents(strictEvents);
            var finalEvidence = extraAdmitted.Single(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.AssistantCompleted,
                        StringComparison.Ordinal))
                .Extensions[
                    FinalOutputAdmissionCodec.EvidenceExtensionName];
            extraAdmitted.First(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.ProviderResultCommitted,
                        StringComparison.Ordinal))
                .Extensions[
                    FinalOutputAdmissionControl.PresentationExtensionName] =
                FinalOutputAdmissionCodec.CreatePresentation(
                    FinalOutputAdmissionCodec.AdmittedPresentationState,
                    "forged_admission",
                    finalEvidence);
            await AssertRecoveryFailsAsync(
                strictRun.RunId,
                extraAdmitted,
                strictCursor);

            var nonStrictRun = Run();
            using (var journal = new JournalCoordinator(
                       store,
                       store,
                       new SystemRuntimeClock(),
                       new GuidRuntimeIdGenerator()))
            {
                await using var runtime = CreateNonStrictRuntime(
                    store,
                    journal,
                    new ScriptedProvider(FinalText("complete")));
                var completed = await runtime.RunAsync(
                    new DurableRunRequest { Run = nonStrictRun }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(RunStates.Completed, completed.Run.State);
            }

            var nonStrictEvents = CloneEvents(
                await store.ReadRunAsync(
                    nonStrictRun.RunId,
                    CancellationToken.None));
            var nonStrictCursor = await store.GetRunCursorAsync(
                nonStrictRun.RunId, cancellationToken: TestContext.Current.CancellationToken);
            nonStrictEvents.Single(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.ProviderResultCommitted,
                        StringComparison.Ordinal))
                .Extensions[
                    FinalOutputAdmissionControl.PresentationExtensionName] =
                FinalOutputAdmissionCodec.CreatePresentation(
                    FinalOutputAdmissionCodec.ProvisionalPresentationState,
                    "forged_presentation");
            await AssertRecoveryFailsAsync(
                nonStrictRun.RunId,
                nonStrictEvents,
                nonStrictCursor);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownBoundsNonCooperativeAdmissionPolicyAndExposesCensus()
    {
        var directory = TempDirectory();
        var policy = new HangingPolicy();
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            await using var runtime = CreateRuntime(
                store,
                journal,
                new ScriptedProvider(
                    ToolCall(
                        "submit-1",
                        FinalOutputAdmissionControl.SubmitToolName,
                        """{"output":{"ok":true},"evidence":[]}""")),
                new RejectingHost(),
                policy,
                new FinalOutputAdmissionOptions
                {
                    Enabled = true,
                    MaxAttempts = 1,
                    MaxConcurrentEvaluations = 1,
                    PolicyTimeout = TimeSpan.FromMilliseconds(50)
                });

            var failed = await runtime.RunAsync(
                new DurableRunRequest { Run = Run() }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RunStates.Failed, failed.Run.State);
            Assert.Equal(
                "final_output_admission_attempts_exhausted",
                failed.ErrorCode);
            Assert.Equal(
                1,
                runtime.DetachedFinalOutputAdmissionPolicyCallCount);

            var watch = Stopwatch.StartNew();
            await runtime.WaitForShutdownDrainAsync(cancellationToken: TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken);
            watch.Stop();

            Assert.True(
                watch.Elapsed < TimeSpan.FromSeconds(1),
                $"Runtime shutdown took {watch.Elapsed}.");
            Assert.False(
                runtime.FinalOutputAdmissionPolicyCallsDrainedOnStop);
            Assert.True(runtime.ShutdownResourceCleanupCompleted);
            Assert.Equal(
                1,
                runtime.DetachedFinalOutputAdmissionPolicyCallCount);

            policy.Release();
            Assert.True(
                await WaitUntilAsync(
                    () => runtime
                              .DetachedFinalOutputAdmissionPolicyCallCount
                          == 0));
        }
        finally
        {
            policy.Release();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledPolicyEvaluationRetainsCapacityUntilItSettles()
    {
        var policy = new HangingPolicy();
        var evaluator = new FinalOutputAdmissionEvaluator(
            policy,
            new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxConcurrentEvaluations = 1,
                PolicyTimeout = TimeSpan.FromMilliseconds(500)
            },
            new BoundedCancellationDispatcher(1));
        var request = PolicyRequest();
        using var cancellation = new CancellationTokenSource();
        Task<FinalOutputAdmissionDecision>? cancelled = null;
        try
        {
            cancelled = evaluator.EvaluateAsync(
                    request,
                    cancellation.Token)
                .AsTask();
            await policy.Started.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelled);
            var capacity = await evaluator.EvaluateAsync(request, TestContext.Current.CancellationToken);

            Assert.False(capacity.Accepted);
            Assert.Equal(
                "final_output_admission_capacity_timeout",
                capacity.ReasonCode);
            Assert.Equal(1, policy.CallCount);

            policy.Release();
            var accepted = await evaluator.EvaluateAsync(request, TestContext.Current.CancellationToken);
            Assert.True(accepted.Accepted);
            Assert.Equal(2, policy.CallCount);
        }
        finally
        {
            cancellation.Cancel();
            policy.Release();
            if (cancelled is not null)
            {
                try
                {
                    await cancelled.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
                }
                catch (Exception) when (cancelled.IsCompleted)
                {
                }
            }
        }
    }

    [Fact]
    public async Task SynchronousPolicyPrefixCannotBypassWallTimeout()
    {
        var policy = new SynchronouslyBlockingPolicy();
        var evaluator = new FinalOutputAdmissionEvaluator(
            policy,
            new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxConcurrentEvaluations = 1,
                PolicyTimeout = TimeSpan.FromMilliseconds(50)
            },
            new BoundedCancellationDispatcher(1));
        try
        {
            var elapsed = Stopwatch.StartNew();
            var evaluation = evaluator.EvaluateAsync(
                    PolicyRequest(),
                    TestContext.Current.CancellationToken)
                .AsTask();
            await policy.Started.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
            var timedOut = await evaluation.WaitAsync(
                TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
            elapsed.Stop();

            Assert.False(timedOut.Accepted);
            Assert.Equal(
                "final_output_admission_policy_timeout",
                timedOut.ReasonCode);
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(1),
                $"Policy wall timeout took {elapsed.Elapsed}.");
            Assert.False(policy.RanOnThreadPool);
            Assert.False(policy.Completed.IsCompleted);

            var capacity = await evaluator.EvaluateAsync(
                PolicyRequest(),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "final_output_admission_capacity_timeout",
                capacity.ReasonCode);
            Assert.Equal(1, policy.CallCount);
        }
        finally
        {
            policy.Release();
        }

        await policy.Completed.WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken);
        var accepted = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.True(accepted.Accepted);
        Assert.Equal(2, policy.CallCount);
    }

    [Fact]
    public async Task PolicyExecutionCapacityIsSharedAcrossEvaluators()
    {
        var dispatcher = new BoundedPolicyExecutionDispatcher(1);
        var firstPolicy = new SynchronouslyBlockingPolicy();
        var secondPolicy = new RecordingPolicy(
            _ => FinalOutputAdmissionDecision.Accept());
        var options = new FinalOutputAdmissionOptions
        {
            Enabled = true,
            MaxConcurrentEvaluations = 1,
            PolicyTimeout = TimeSpan.FromMilliseconds(50)
        };
        var first = new FinalOutputAdmissionEvaluator(
            firstPolicy,
            options,
            new BoundedCancellationDispatcher(1),
            dispatcher);
        var second = new FinalOutputAdmissionEvaluator(
            secondPolicy,
            options,
            new BoundedCancellationDispatcher(1),
            dispatcher);

        try
        {
            var timedOut = await first.EvaluateAsync(
                    PolicyRequest(),
                    TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(
                "final_output_admission_policy_timeout",
                timedOut.ReasonCode);
            Assert.Equal(1, dispatcher.ActiveExecutions);

            var rejected = await second.EvaluateAsync(
                PolicyRequest(),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "final_output_admission_policy_capacity_exhausted",
                rejected.ReasonCode);
            Assert.Empty(secondPolicy.Requests);
        }
        finally
        {
            firstPolicy.Release();
        }

        await firstPolicy.Completed.WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(
            await WaitUntilAsync(() => dispatcher.ActiveExecutions == 0));
        var accepted = await second.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.True(accepted.Accepted);
        Assert.Single(secondPolicy.Requests);
    }

    [Fact]
    public async Task PolicyValueTaskResultIsReisolatedAndBounded()
    {
        var dispatcher = new BoundedPolicyExecutionDispatcher(1);
        var policy = new BlockingResultPolicy();
        var evaluator = new FinalOutputAdmissionEvaluator(
            policy,
            new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxConcurrentEvaluations = 1,
                PolicyTimeout = TimeSpan.FromMilliseconds(50)
            },
            new BoundedCancellationDispatcher(1),
            dispatcher);
        var evaluation = evaluator.EvaluateAsync(
                PolicyRequest(),
                TestContext.Current.CancellationToken)
            .AsTask();
        Task? completionTrigger = null;

        try
        {
            await policy.RegistrationEntered.WaitAsync(
                TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
            completionTrigger = Task.Factory.StartNew(
                policy.Complete,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await policy.ResultEntered.WaitAsync(
                TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
            var timedOut = await evaluation.WaitAsync(
                TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(timedOut.Accepted);
            Assert.Equal(
                "final_output_admission_policy_timeout",
                timedOut.ReasonCode);
            Assert.False(policy.ResultRanOnThreadPool);
            Assert.Equal(1, dispatcher.ActiveExecutions);
        }
        finally
        {
            policy.ReleaseResult();
            if (completionTrigger is not null)
            {
                await completionTrigger.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        Assert.True(
            await WaitUntilAsync(() => dispatcher.ActiveExecutions == 0));
        var accepted = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.True(accepted.Accepted);
        Assert.Equal(2, policy.CallCount);
    }

    [Fact]
    public async Task BlockingCancellationCallbackCannotExtendWallTimeout()
    {
        var policy = new BlockingCancellationPolicy();
        var evaluator = new FinalOutputAdmissionEvaluator(
            policy,
            new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxConcurrentEvaluations = 1,
                PolicyTimeout = TimeSpan.FromMilliseconds(50)
            },
            new BoundedCancellationDispatcher(1));
        var fallback = policy.ReleaseAfterAsync(
            TimeSpan.FromSeconds(1));

        var timedOut = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);

        Assert.False(timedOut.Accepted);
        Assert.Equal(
            "final_output_admission_policy_timeout",
            timedOut.ReasonCode);
        Assert.False(policy.Completed.IsCompleted);
        await policy.CallbackStarted.WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken);

        var capacity = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "final_output_admission_capacity_timeout",
            capacity.ReasonCode);
        Assert.Equal(1, policy.CallCount);

        policy.Release();
        await fallback;
        await policy.Completed.WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken);
        var accepted = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.True(accepted.Accepted);
        Assert.Equal(2, policy.CallCount);
    }

    [Fact]
    public async Task SettledCancellationDispatchDoesNotLeakGlobalCapacity()
    {
        var policy = new MultiHangingPolicy();
        var dispatcher = new BoundedCancellationDispatcher(1);
        var evaluator = new FinalOutputAdmissionEvaluator(
            policy,
            new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxConcurrentEvaluations = 2,
                PolicyTimeout = TimeSpan.FromMilliseconds(50)
            },
            dispatcher);

        var first = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "final_output_admission_policy_timeout",
            first.ReasonCode);
        Assert.True(
            await WaitUntilAsync(
                () => dispatcher.ActiveReservations == 0));

        var second = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "final_output_admission_policy_timeout",
            second.ReasonCode);
        Assert.True(
            await WaitUntilAsync(
                () => dispatcher.ActiveReservations == 0));
        Assert.Equal(2, policy.CallCount);
        Assert.Equal(2, policy.CancellationCount);

        var saturated = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "final_output_admission_capacity_timeout",
            saturated.ReasonCode);

        policy.ReleaseAll();
        Assert.True(
            await WaitUntilAsync(
                () => policy.CompletedCount == 2));
        var accepted = await evaluator.EvaluateAsync(
            PolicyRequest(),
            TestContext.Current.CancellationToken);
        Assert.True(accepted.Accepted);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TestWaitTimeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private static DurableAgentRuntime CreateRuntime(
        FileSessionStore store,
        JournalCoordinator journal,
        IStreamingModelProvider provider,
        IGameHost host,
        IFinalOutputAdmissionPolicy policy,
        params ToolDescriptor[] tools)
    {
        return CreateRuntime(
            store,
            journal,
            provider,
            host,
            policy,
            new FinalOutputAdmissionOptions
            {
                Enabled = true,
                MaxAttempts = 4,
                PolicyTimeout = TimeSpan.FromSeconds(1)
            },
            tools);
    }

    private static DurableAgentRuntime CreateNonStrictRuntime(
        FileSessionStore store,
        JournalCoordinator journal,
        IStreamingModelProvider provider)
    {
        return CreateNonStrictRuntime(
            store,
            journal,
            provider,
            new RejectingHost());
    }

    private static DurableAgentRuntime CreateNonStrictRuntime(
        FileSessionStore store,
        JournalCoordinator journal,
        IStreamingModelProvider provider,
        IGameHost host,
        params ToolDescriptor[] tools)
    {
        var toolRegistry = new ToolCatalogRegistry();
        toolRegistry.Replace(tools);
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
                new GuidRuntimeIdGenerator()),
            host,
            journal,
            new RunRecovery(store, store, journal),
            toolRegistry,
            new SkillCatalogRegistry(),
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            new SystemRuntimeClock(),
            new GuidRuntimeIdGenerator(),
            new DurableAgentRuntimeOptions
            {
                ModelId = "test-model"
            });
    }

    private static DurableAgentRuntime CreateRuntimeWithMemory(
        FileSessionStore store,
        JournalCoordinator journal,
        IStreamingModelProvider provider,
        IFinalOutputAdmissionPolicy admissionPolicy,
        RuntimeMemoryLifecycle memory,
        IRuntimeMemoryPolicy memoryPolicy)
    {
        return CreateRuntimeWithMemory(
            store,
            store,
            journal,
            provider,
            admissionPolicy,
            memory,
            memoryPolicy);
    }

    private static DurableAgentRuntime CreateRuntimeWithMemory(
        IDurableSessionStore store,
        IOperationLedger ledger,
        JournalCoordinator journal,
        IStreamingModelProvider provider,
        IFinalOutputAdmissionPolicy admissionPolicy,
        RuntimeMemoryLifecycle memory,
        IRuntimeMemoryPolicy memoryPolicy)
    {
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
                new GuidRuntimeIdGenerator()),
            new RejectingHost(),
            journal,
            new RunRecovery(store, ledger, journal),
            new ToolCatalogRegistry(),
            new SkillCatalogRegistry(),
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            new SystemRuntimeClock(),
            new GuidRuntimeIdGenerator(),
            new DurableAgentRuntimeOptions
            {
                ModelId = "test-model",
                FinalOutputAdmission = new FinalOutputAdmissionOptions
                {
                    Enabled = true,
                    MaxAttempts = 4,
                    PolicyTimeout = TimeSpan.FromSeconds(1)
                }
            },
            memoryLifecycle: memory,
            memoryPolicy: memoryPolicy,
            finalOutputAdmissionPolicy: admissionPolicy);
    }

    private static List<RuntimeEvent> CloneEvents(
        IReadOnlyList<RuntimeEvent> events)
    {
        return events
            .Select(
                item => ProtocolJson.DeserializeRuntimeEvent(
                    ProtocolJson.Serialize(item)))
            .ToList();
    }

    private static async Task AssertRecoveryFailsAsync(
        string runId,
        IReadOnlyList<RuntimeEvent> events,
        RunJournalCursor cursor)
    {
        await using var store = new StaticRecoveryStore(events, cursor);
        using var journal = new JournalCoordinator(
            store,
            store,
            new SystemRuntimeClock(),
            new GuidRuntimeIdGenerator());
        var recovery = new RunRecovery(store, store, journal);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => recovery.LoadAsync(runId, CancellationToken.None)
                .AsTask());
    }

    private static DurableAgentRuntime CreateRuntime(
        IDurableSessionStore store,
        IOperationLedger ledger,
        JournalCoordinator journal,
        IStreamingModelProvider provider,
        IGameHost host,
        IFinalOutputAdmissionPolicy policy,
        FinalOutputAdmissionOptions admissionOptions,
        params ToolDescriptor[] tools)
    {
        var toolRegistry = new ToolCatalogRegistry();
        toolRegistry.Replace(tools);
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
                new GuidRuntimeIdGenerator()),
            host,
            journal,
            new RunRecovery(store, ledger, journal),
            toolRegistry,
            new SkillCatalogRegistry(),
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            new SystemRuntimeClock(),
            new GuidRuntimeIdGenerator(),
            new DurableAgentRuntimeOptions
            {
                ModelId = "test-model",
                FinalOutputAdmission = admissionOptions
            },
            finalOutputAdmissionPolicy: policy);
    }

    private static DurableAgentRuntime CreateRuntime(
        FileSessionStore store,
        JournalCoordinator journal,
        IStreamingModelProvider provider,
        IGameHost host,
        IFinalOutputAdmissionPolicy policy,
        FinalOutputAdmissionOptions admissionOptions,
        params ToolDescriptor[] tools)
    {
        return CreateRuntime(
            store,
            store,
            journal,
            provider,
            host,
            policy,
            admissionOptions,
            tools);
    }

    private static FinalOutputAdmissionRequest PolicyRequest()
    {
        var run = Run();
        return new FinalOutputAdmissionRequest(
            run,
            "turn-1",
            Array.Empty<ContextCandidate>(),
            new FinalOutputProposal(
                "submit-1",
                null,
                Json("""{"ok":true}"""),
                Array.Empty<FinalOutputCommittedEvidence>()),
            Array.Empty<FinalOutputCommittedEvidence>());
    }

    private static IEnumerable<ModelStreamEvent> EvidenceSubmission(
        StreamingModelRequest request,
        string toolCallId,
        bool forgeSource)
    {
        var result = request.Messages
            .SelectMany(message => message.Parts)
            .Where(
                part => string.Equals(
                            part.Type,
                            NormalizedPartTypes.ToolResult,
                            StringComparison.Ordinal)
                        && string.Equals(
                            part.ToolName,
                            "set_flag",
                            StringComparison.Ordinal))
            .Select(part => part.Json!.Value)
            .Last();
        var receipt = ProtocolJson.DeserializeActionReceipt(
            result.GetProperty("receipt").GetRawText());
        var sourceEventId = result
            .GetProperty("evidenceReference")
            .GetProperty(
                FinalOutputAdmissionControl
                    .EvidenceSourceEventIdPropertyName)
            .GetString()!;
        if (forgeSource)
        {
            sourceEventId += "-forged";
        }

        return ToolCall(
            toolCallId,
            FinalOutputAdmissionControl.SubmitToolName,
            JsonSerializer.Serialize(
                new
                {
                    output = new { committed = true },
                    evidence = new[]
                    {
                        new
                        {
                            operationId = receipt.OperationId,
                            revision = receipt.Revision,
                            sourceEventId
                        }
                    }
                }))(request);
    }

    private static Func<
        StreamingModelRequest,
        IEnumerable<ModelStreamEvent>> FinalText(string text)
    {
        return request => new[]
        {
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = text
            },
            Usage(request.StreamAttemptId, 1),
            Completed(request.StreamAttemptId, 2, "stop")
        };
    }

    private static Func<
        StreamingModelRequest,
        IEnumerable<ModelStreamEvent>> ToolCall(
        string toolCallId,
        string name,
        string arguments)
    {
        return request => new[]
        {
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = toolCallId,
                ToolNameDelta = name,
                ArgumentsJsonDelta = arguments
            },
            Usage(request.StreamAttemptId, 1),
            Completed(request.StreamAttemptId, 2, "tool_calls")
        };
    }

    private static Func<
        StreamingModelRequest,
        IEnumerable<ModelStreamEvent>> ToolCalls(
        params (
            string ToolCallId,
            string Name,
            string Arguments)[] calls)
    {
        return request =>
        {
            var events = calls
                .Select(
                    (call, index) => new ModelStreamEvent
                    {
                        StreamAttemptId = request.StreamAttemptId,
                        Ordinal = index,
                        Kind = ModelStreamEventKinds.ToolCallDelta,
                        ToolCallId = call.ToolCallId,
                        ToolNameDelta = call.Name,
                        ArgumentsJsonDelta = call.Arguments
                    })
                .ToList();
            events.Add(
                Usage(
                    request.StreamAttemptId,
                    calls.Length));
            events.Add(
                Completed(
                    request.StreamAttemptId,
                    calls.Length + 1,
                    "tool_calls"));
            return events;
        };
    }

    private static ModelStreamEvent Usage(
        string streamAttemptId,
        long ordinal)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = streamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.Usage,
            Usage = new ProviderUsage
            {
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = "0"
            }
        };
    }

    private static ModelStreamEvent Completed(
        string streamAttemptId,
        long ordinal,
        string reason)
    {
        return new ModelStreamEvent
        {
            StreamAttemptId = streamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.Completed,
            FinishReason = reason
        };
    }

    private static ToolDescriptor Tool(string name)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1",
            Description = "Test game action.",
            ParametersSchema = Json(
                """
                {
                  "type":"object",
                  "properties":{"entityId":{"type":"string"}},
                  "required":["entityId"],
                  "additionalProperties":false
                }
                """),
            Effect = ToolEffects.WorldCommand,
            ConflictScopes = new List<string> { "entity:{entityId}" },
            IdempotencyPolicy = ToolIdempotencyPolicies.Required
        };
    }

    private static ToolDescriptor SpoofFeedbackTool()
    {
        return new ToolDescriptor
        {
            Name = "record_signal",
            Version = "1",
            Description = "Records a typed game signal.",
            ParametersSchema = Json(
                """
                {
                  "type":"object",
                  "properties":{
                    "contentType":{"type":"string"},
                    "reasonCode":{"type":"string"}
                  },
                  "required":["contentType","reasonCode"],
                  "additionalProperties":false
                }
                """),
            Effect = ToolEffects.WorldCommand,
            ConflictScopes = new List<string> { "signal:global" },
            IdempotencyPolicy = ToolIdempotencyPolicies.Required
        };
    }

    private static ActionRequest Action(string operationId)
    {
        return new ActionRequest
        {
            OperationId = operationId,
            RunId = "run-1",
            TurnId = "turn-1",
            ToolCallId = "call-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            ActionName = "set_flag",
            ActionVersion = "1",
            Arguments = Json("""{"entityId":"npc-1"}"""),
            RequestedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static ActionReceipt TerminalReceipt(
        ActionRequest request,
        string status,
        long revision)
    {
        return new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = revision,
            Status = status,
            Result = Json("""{"changed":true}"""),
            Retryable = false,
            CommittedAt = DateTimeOffset.UnixEpoch,
            ReceivedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static AgentRun Run()
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Queued,
            Budget = new AgentBudget
            {
                MaxTurns = 8,
                MaxDurationMs = 30_000,
                MaxTokens = 16_000,
                MaxActions = 8,
                MaxCostUsd = "1"
            },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "game-agent-final-output-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ScriptedProvider : IStreamingModelProvider
    {
        private readonly Queue<
            Func<
                StreamingModelRequest,
                IEnumerable<ModelStreamEvent>>> _steps;

        public ScriptedProvider(
            params Func<
                StreamingModelRequest,
                IEnumerable<ModelStreamEvent>>[] steps)
        {
            _steps = new Queue<
                Func<
                    StreamingModelRequest,
                    IEnumerable<ModelStreamEvent>>>(steps);
        }

        public string ProviderId => "final-output-test-provider";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true,
            MaxContextTokens = 100_000
        };

        public List<StreamingModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException(
                    "No scripted provider step remains.");
            }

            foreach (var item in _steps.Dequeue()(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingPolicy : IFinalOutputAdmissionPolicy
    {
        private readonly Func<
            FinalOutputAdmissionRequest,
            FinalOutputAdmissionDecision> _evaluate;
        private readonly string _policyId;
        private readonly string _version;

        public RecordingPolicy(
            Func<
                FinalOutputAdmissionRequest,
                FinalOutputAdmissionDecision> evaluate,
            string policyId = "test-final-output-policy",
            string version = "1")
        {
            _evaluate = evaluate;
            _policyId = policyId;
            _version = version;
        }

        public string PolicyId => _policyId;

        public string Version => _version;

        public List<FinalOutputAdmissionRequest> Requests { get; } = new();

        public ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
            FinalOutputAdmissionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return new ValueTask<FinalOutputAdmissionDecision>(
                _evaluate(request));
        }
    }

    private sealed class RecordingMemoryPolicy : IRuntimeMemoryPolicy
    {
        public string PolicyId => "recording-memory-policy";

        public string Version => "1";

        public List<RuntimeMemoryCommitContext> Commits { get; } = new();

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            _ = context;
            return null;
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            Commits.Add(context);
            return Array.Empty<MemoryMutation>();
        }
    }

    private sealed class ThrowingMemoryPolicy : IRuntimeMemoryPolicy
    {
        private int _calls;

        public string PolicyId => "throwing-memory-policy";

        public string Version => "1";

        public int CallCount => Volatile.Read(ref _calls);

        public RuntimeMemoryRecallPlan? PlanRecall(
            RuntimeMemoryRecallContext context)
        {
            _ = context;
            return null;
        }

        public IReadOnlyList<MemoryMutation> SelectCommittedMutations(
            RuntimeMemoryCommitContext context)
        {
            _ = context;
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException(
                "Simulated memory policy failure.");
        }
    }

    private sealed class HangingPolicy : IFinalOutputAdmissionPolicy
    {
        private readonly TaskCompletionSource<
            FinalOutputAdmissionDecision> _first =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public string PolicyId => "hanging-policy";

        public string Version => "1";

        public int CallCount => Volatile.Read(ref _calls);

        public Task Started => _started.Task;

        public ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
            FinalOutputAdmissionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _started.TrySetResult();
            }

            return call == 1
                ? new ValueTask<FinalOutputAdmissionDecision>(_first.Task)
                : new ValueTask<FinalOutputAdmissionDecision>(
                    FinalOutputAdmissionDecision.Accept());
        }

        public void Release()
        {
            _first.TrySetResult(FinalOutputAdmissionDecision.Accept());
        }
    }

    private sealed class SynchronouslyBlockingPolicy :
        IFinalOutputAdmissionPolicy
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _ranOnThreadPool;

        public string PolicyId => "synchronously-blocking-policy";

        public string Version => "1";

        public int CallCount => Volatile.Read(ref _calls);

        public Task Completed => _completed.Task;

        public Task Started => _started.Task;

        public bool RanOnThreadPool =>
            Volatile.Read(ref _ranOnThreadPool) != 0;

        public ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
            FinalOutputAdmissionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            if (Interlocked.Increment(ref _calls) == 1)
            {
                if (Thread.CurrentThread.IsThreadPoolThread)
                {
                    Volatile.Write(ref _ranOnThreadPool, 1);
                }

                _started.TrySetResult();
                _release.Wait();
                _completed.TrySetResult();
            }

            return new ValueTask<FinalOutputAdmissionDecision>(
                FinalOutputAdmissionDecision.Accept());
        }

        public void Release()
        {
            _release.Set();
        }

    }

    private sealed class BlockingResultPolicy :
        IFinalOutputAdmissionPolicy,
        IValueTaskSource<FinalOutputAdmissionDecision>
    {
        private readonly ManualResetEventSlim _resultRelease = new(false);
        private readonly TaskCompletionSource _registrationEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resultEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<object?>? _continuation;
        private object? _continuationState;
        private int _calls;
        private int _completed;
        private int _resultRanOnThreadPool;

        public string PolicyId => "blocking-result-policy";

        public string Version => "1";

        public int CallCount => Volatile.Read(ref _calls);

        public Task RegistrationEntered => _registrationEntered.Task;

        public Task ResultEntered => _resultEntered.Task;

        public bool ResultRanOnThreadPool =>
            Volatile.Read(ref _resultRanOnThreadPool) != 0;

        public ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
            FinalOutputAdmissionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Interlocked.Increment(ref _calls) == 1
                ? new ValueTask<FinalOutputAdmissionDecision>(this, token: 0)
                : new ValueTask<FinalOutputAdmissionDecision>(
                    FinalOutputAdmissionDecision.Accept());
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            _ = token;
            return Volatile.Read(ref _completed) == 0
                ? ValueTaskSourceStatus.Pending
                : ValueTaskSourceStatus.Succeeded;
        }

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _ = token;
            _ = flags;
            _continuation = continuation;
            _continuationState = state;
            _registrationEntered.TrySetResult();
        }

        public FinalOutputAdmissionDecision GetResult(short token)
        {
            _ = token;
            Volatile.Write(
                ref _resultRanOnThreadPool,
                Thread.CurrentThread.IsThreadPoolThread ? 1 : 0);
            _resultEntered.TrySetResult();
            _resultRelease.Wait();
            return FinalOutputAdmissionDecision.Accept();
        }

        public void Complete()
        {
            Volatile.Write(ref _completed, 1);
            (_continuation
             ?? throw new InvalidOperationException(
                 "No policy continuation was registered."))(
                _continuationState);
        }

        public void ReleaseResult()
        {
            _resultRelease.Set();
        }
    }

    private sealed class BlockingCancellationPolicy :
        IFinalOutputAdmissionPolicy
    {
        private readonly ManualResetEventSlim _callbackRelease = new(false);
        private readonly TaskCompletionSource<
            FinalOutputAdmissionDecision> _first =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _callbackStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public string PolicyId => "blocking-cancellation-policy";

        public string Version => "1";

        public int CallCount => Volatile.Read(ref _calls);

        public Task CallbackStarted => _callbackStarted.Task;

        public Task Completed => _completed.Task;

        public ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
            FinalOutputAdmissionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            if (Interlocked.Increment(ref _calls) != 1)
            {
                return new ValueTask<FinalOutputAdmissionDecision>(
                    FinalOutputAdmissionDecision.Accept());
            }

            cancellationToken.Register(
                () =>
                {
                    _callbackStarted.TrySetResult();
                    _callbackRelease.Wait();
                });
            return AwaitFirstAsync();
        }

        public void Release()
        {
            _first.TrySetResult(FinalOutputAdmissionDecision.Accept());
            _callbackRelease.Set();
        }

        public async Task ReleaseAfterAsync(TimeSpan delay)
        {
            await Task.Delay(delay);
            Release();
        }

        private async ValueTask<FinalOutputAdmissionDecision>
            AwaitFirstAsync()
        {
            try
            {
                return await _first.Task;
            }
            finally
            {
                _completed.TrySetResult();
            }
        }
    }

    private sealed class MultiHangingPolicy :
        IFinalOutputAdmissionPolicy
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource<
            FinalOutputAdmissionDecision>> _pending = new();
        private int _calls;
        private int _cancellations;
        private int _completed;

        public string PolicyId => "multi-hanging-policy";

        public string Version => "1";

        public int CallCount => Volatile.Read(ref _calls);

        public int CancellationCount => Volatile.Read(ref _cancellations);

        public int CompletedCount => Volatile.Read(ref _completed);

        public ValueTask<FinalOutputAdmissionDecision> EvaluateAsync(
            FinalOutputAdmissionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var call = Interlocked.Increment(ref _calls);
            if (call > 2)
            {
                return new ValueTask<FinalOutputAdmissionDecision>(
                    FinalOutputAdmissionDecision.Accept());
            }

            cancellationToken.Register(
                () => Interlocked.Increment(ref _cancellations));
            var pending = new TaskCompletionSource<
                FinalOutputAdmissionDecision>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _pending.Add(pending);
            }

            return AwaitPendingAsync(pending.Task);
        }

        public void ReleaseAll()
        {
            TaskCompletionSource<FinalOutputAdmissionDecision>[] pending;
            lock (_sync)
            {
                pending = _pending.ToArray();
            }

            foreach (var item in pending)
            {
                item.TrySetResult(FinalOutputAdmissionDecision.Accept());
            }
        }

        private async ValueTask<FinalOutputAdmissionDecision>
            AwaitPendingAsync(Task<FinalOutputAdmissionDecision> pending)
        {
            try
            {
                return await pending;
            }
            finally
            {
                Interlocked.Increment(ref _completed);
            }
        }
    }

    private sealed class RejectingHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "No host action should be dispatched.");
        }
    }

    private sealed class UnknownHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 0,
                    Status = ReceiptStatuses.Unknown,
                    ErrorCode = "result_unknown",
                    Retryable = true,
                    CommittedAt = null,
                    ReceivedAt = DateTimeOffset.UtcNow
                });
        }
    }

    private sealed class TerminalReconciler : IGameOperationReconciler
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = Json("""{"reconciled":true}"""),
                    Retryable = false,
                    CommittedAt = now,
                    ReceivedAt = now
                });
        }
    }

    private sealed class SucceedingHost : IGameHost
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 0,
                    Status = ReceiptStatuses.Succeeded,
                    Result = Json("""{"changed":true}"""),
                    Retryable = false,
                    CommittedAt = now,
                    ReceivedAt = now
                });
        }
    }

    private sealed class RecordingPublisher :
        INonBlockingRuntimeEventPublisher
    {
        public List<RuntimeEvent> Events { get; } = new();

        public void Publish(RuntimeEvent runtimeEvent)
        {
            Events.Add(runtimeEvent);
        }
    }

    private sealed class CrashAfterTurnCompletedStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private int _crashed;

        public CrashAfterTurnCompletedStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public bool Crashed => Volatile.Read(ref _crashed) != 0;

        public async ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            ThrowIfCrashed();
            await _inner.AppendAsync(runtimeEvent, cancellationToken);
            CrashIfTurnCompleted(runtimeEvent);
        }

        public async ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfCrashed();
            var result = await _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
            CrashIfTurnCompleted(runtimeEvent);
            return result;
        }

        public async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            ThrowIfCrashed();
            var result = await _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
            if (runtimeEvents.Any(
                    item => string.Equals(
                        item.Kind,
                        RuntimeEventKinds.TurnCompleted,
                        StringComparison.Ordinal)))
            {
                Crash();
            }

            return result;
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
            return _inner.GetOperationAsync(
                operationId,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(
                runId,
                cancellationToken);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfCrashed();
            return _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private void CrashIfTurnCompleted(RuntimeEvent runtimeEvent)
        {
            if (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.TurnCompleted,
                    StringComparison.Ordinal))
            {
                Crash();
            }
        }

        private void Crash()
        {
            Interlocked.Exchange(ref _crashed, 1);
            throw new IOException(
                "Simulated process loss after durable turn completion.");
        }

        private void ThrowIfCrashed()
        {
            if (Crashed)
            {
                throw new IOException("Simulated process is unavailable.");
            }
        }
    }

    private sealed class CrashAfterAtomicBatchStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private readonly Func<IReadOnlyList<RuntimeEvent>, bool>
            _shouldCrash;
        private readonly string _crashMessage;
        private int _crashed;

        public CrashAfterAtomicBatchStore(
            FileSessionStore inner,
            Func<IReadOnlyList<RuntimeEvent>, bool> shouldCrash,
            string crashMessage)
        {
            _inner = inner;
            _shouldCrash = shouldCrash;
            _crashMessage = crashMessage;
        }

        public bool Crashed => Volatile.Read(ref _crashed) != 0;

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            ThrowIfCrashed();
            return _inner.AppendAsync(runtimeEvent, cancellationToken);
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfCrashed();
            return _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            ThrowIfCrashed();
            var result = await _inner.AppendAtomicBatchAsync(
                runtimeEvents,
                expectedRunRevision,
                cancellationToken);
            if (_shouldCrash(runtimeEvents))
            {
                Interlocked.Exchange(ref _crashed, 1);
                throw new IOException(_crashMessage);
            }

            return result;
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
            return _inner.GetOperationAsync(
                operationId,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            return _inner.ReadPendingOperationsAsync(
                runId,
                cancellationToken);
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfCrashed();
            return _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private void ThrowIfCrashed()
        {
            if (Crashed)
            {
                throw new IOException("Simulated process is unavailable.");
            }
        }
    }

    private sealed class StaticRecoveryStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly IReadOnlyList<RuntimeEvent> _events;
        private readonly RunJournalCursor _cursor;

        public StaticRecoveryStore(
            IReadOnlyList<RuntimeEvent> events,
            RunJournalCursor cursor)
        {
            _events = events;
            _cursor = cursor;
        }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(_events);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RunJournalCursor>(_cursor);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<OperationLedgerEntry?>(
                result: null);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<OperationLedgerEntry>>(
                Array.Empty<OperationLedgerEntry>());
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
