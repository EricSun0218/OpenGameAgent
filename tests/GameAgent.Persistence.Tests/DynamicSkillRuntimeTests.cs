using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class DynamicSkillRuntimeTests
{
    [Fact]
    public async Task ModelCanSearchStructuredCatalogActivateExactlyAndUseSkillNextTurn()
    {
        var manifest = Skill(
            "market-dialogue",
            "Handle market negotiations.",
            "DYNAMIC_SKILL_MARKER");
        string skillDigest = string.Empty;
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            skills => new ScriptedProvider(
                request =>
                {
                    AssertSkillControls(request);
                    Assert.DoesNotContain(
                        "DYNAMIC_SKILL_MARKER",
                        Payloads(request));
                    return ToolCalls(
                        request,
                        new ToolCallSpec(
                            "search-skill",
                            SkillRuntimeControlNames.Search,
                            """
                            {
                              "query": {
                                "capability": "market",
                                "input": { "kind": "trade_event" }
                              },
                              "limit": 4
                            }
                            """));
                },
                request =>
                {
                    Assert.Contains(
                        "market-dialogue",
                        Payloads(request));
                    return ToolCalls(
                        request,
                        new ToolCallSpec(
                            "activate-skill-1",
                            SkillRuntimeControlNames.Activate,
                            ActivationArguments(
                                "market-dialogue",
                                skillDigest)),
                        new ToolCallSpec(
                            "activate-skill-2",
                            SkillRuntimeControlNames.Activate,
                            ActivationArguments(
                                "market-dialogue",
                                skillDigest)));
                },
                request =>
                {
                    Assert.Contains(
                        "DYNAMIC_SKILL_MARKER",
                        Payloads(request));
                    Assert.DoesNotContain(
                        request.Tools,
                        tool => SkillRuntimeControlNames.IsReserved(
                            tool.Name));
                    return FinalEvents(request);
                }));
        skillDigest = AssertSkill(rig, "market-dialogue").ContentDigest;
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = run });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(3, rig.Provider.Requests.Count);
        var transcriptPayload = string.Join(
            "\n",
            outcome.Transcript
                .Where(message => message.Role == NormalizedRoles.Tool)
                .SelectMany(message => message.Parts)
                .Where(part => part.Json.HasValue)
                .Select(part => part.Json!.Value.GetRawText()));
        Assert.Contains(
            SkillRuntimeReasonCodes.ActivatedByModel,
            transcriptPayload);
        Assert.Contains(
            SkillRuntimeReasonCodes.AlreadyActivated,
            transcriptPayload);

        var events = await rig.Store.ReadRunAsync(run.RunId, default);
        var atomicCheckpointIndex = Assert.Single(
                events.Select((item, index) => (item, index)),
                value => value.item.Kind == RuntimeEventKinds.RunCheckpoint
                         && value.item.EventId.StartsWith(
                             "skill-activation-checkpoint:",
                             StringComparison.Ordinal))
            .index;
        Assert.Equal(
            RuntimeEventKinds.ToolDisclosureChanged,
            events[atomicCheckpointIndex + 1].Kind);
        Assert.Equal(
            RuntimeEventKinds.TranscriptMessage,
            events[atomicCheckpointIndex + 2].Kind);
        Assert.Equal(
            events[atomicCheckpointIndex].Sequence + 1,
            events[atomicCheckpointIndex + 1].Sequence);
        Assert.Equal(
            events[atomicCheckpointIndex].Sequence + 2,
            events[atomicCheckpointIndex + 2].Sequence);
        var activationCheckpoints = events.Where(
                item => item.Kind == RuntimeEventKinds.TurnCompleted
                        && item.Payload
                            .GetProperty("extensions")
                            .TryGetProperty(
                                SkillActivationStateCodec.ExtensionName,
                                out var state)
                        && state.GetProperty("activations")
                            .GetArrayLength() == 1)
            .ToArray();
        var activationCheckpoint = Assert.Single(activationCheckpoints);
        Assert.Equal(
            "market-dialogue",
            activationCheckpoint.Payload
                .GetProperty("extensions")
                .GetProperty(SkillActivationStateCodec.ExtensionName)
                .GetProperty("activations")[0]
                .GetProperty("skillId")
                .GetString());
        var snapshots = events
            .Where(item => item.Kind == RuntimeEventKinds.TurnSnapshot)
            .ToArray();
        Assert.Equal(3, snapshots.Length);
        Assert.Single(
            snapshots[^1].Payload
                .GetProperty("extensions")
                .GetProperty(SkillActivationStateCodec.ExtensionName)
                .GetProperty("activations")
                .EnumerateArray());
    }

    [Theory]
    [InlineData("fragment_count", "skill_prompt_fragment_count_exceeded")]
    [InlineData("prompt_bytes", "skill_prompt_bytes_exceeded")]
    [InlineData("reference_count", "skill_reference_count_exceeded")]
    public async Task DynamicActivationPreflightsFullDisclosureBudgetWithoutDurableSideEffects(
        string budgetCase,
        string expectedReasonCode)
    {
        var helper = Tool("skill.helper", ToolEffects.PureRead);
        helper.Visibility = ToolVisibilities.Deferred;
        var manifest = Skill(
            "budget-bound",
            "Must not poison durable state when activation exceeds a budget.",
            "BUDGET_BOUND_SKILL_MARKER",
            requiredTools: new[] { "skill.helper@1.0.0" });
        string skillDigest = string.Empty;
        var budget = budgetCase switch
        {
            "fragment_count" => new SkillDisclosureBudget(
                maxActivatedSkills: 1,
                maxPromptFragments: 0,
                maxPromptUtf8Bytes: 1_000,
                maxReferences: 8),
            "prompt_bytes" => new SkillDisclosureBudget(
                maxActivatedSkills: 1,
                maxPromptFragments: 2,
                maxPromptUtf8Bytes: 1,
                maxReferences: 8),
            "reference_count" => new SkillDisclosureBudget(
                maxActivatedSkills: 1,
                maxPromptFragments: 2,
                maxPromptUtf8Bytes: 1_000,
                maxReferences: 0),
            _ => throw new ArgumentOutOfRangeException(
                nameof(budgetCase))
        };
        await using var rig = new RuntimeRig(
            new[] { helper },
            new[] { manifest },
            _ => new ScriptedProvider(
                request => ToolCalls(
                    request,
                    new ToolCallSpec(
                        "activate-budget-bound",
                        SkillRuntimeControlNames.Activate,
                        ActivationArguments(
                            "budget-bound",
                            skillDigest))),
                request =>
                {
                    Assert.DoesNotContain(
                        "BUDGET_BOUND_SKILL_MARKER",
                        Payloads(request));
                    return FinalEvents(request);
                }),
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "dynamic-skill-test-model",
                MaxConcurrentProviderCalls = 1,
                SkillDisclosureBudget = budget
            });
        skillDigest = AssertSkill(rig, "budget-bound").ContentDigest;
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = run });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(2, rig.Provider.Requests.Count);
        var activationResult = Assert.Single(
            outcome.Transcript
                .Where(message => message.Role == NormalizedRoles.Tool)
                .SelectMany(message => message.Parts)
                .Where(part => part.Json.HasValue)
                .Select(part => part.Json!.Value),
            value => value.TryGetProperty(
                         "reasonCode",
                         out var reason)
                     && reason.GetString() == expectedReasonCode);
        Assert.False(
            activationResult.GetProperty("activated").GetBoolean());

        var events = await rig.Store.ReadRunAsync(run.RunId, default);
        Assert.DoesNotContain(
            events,
            item => item.Kind == RuntimeEventKinds.RunCheckpoint
                    && item.EventId.StartsWith(
                        "skill-activation-checkpoint:",
                        StringComparison.Ordinal));
        Assert.DoesNotContain(
            events,
            item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged
                    && item.Payload.GetRawText().Contains(
                        "skill.helper",
                        StringComparison.Ordinal));
        Assert.DoesNotContain(
            events,
            item => item.Payload.GetRawText().Contains(
                        "BUDGET_BOUND_SKILL_MARKER",
                        StringComparison.Ordinal));
    }

    [Fact]
    public async Task DynamicActivationBudgetsTheCompleteExistingAndProposedSkillSet()
    {
        var helper = Tool("skill.helper", ToolEffects.PureRead);
        helper.Visibility = ToolVisibilities.Deferred;
        var existing = Skill(
            "already-active",
            "Already active.",
            "ACTIVE_A");
        var proposed = Skill(
            "new-skill",
            "Individually valid but too large in the combined active set.",
            "INACTIVE_B",
            requiredTools: new[] { "skill.helper@1.0.0" });
        string proposedDigest = string.Empty;
        await using var rig = new RuntimeRig(
            new[] { helper },
            new[] { existing, proposed },
            _ => new ScriptedProvider(
                request =>
                {
                    Assert.Contains("ACTIVE_A", Payloads(request));
                    Assert.DoesNotContain("INACTIVE_B", Payloads(request));
                    Assert.DoesNotContain(
                        request.Tools,
                        tool => tool.Name == "skill.helper");
                    return ToolCalls(
                        request,
                        new ToolCallSpec(
                            "activate-combined-budget",
                            SkillRuntimeControlNames.Activate,
                            ActivationArguments(
                                "new-skill",
                                proposedDigest)));
                },
                request =>
                {
                    Assert.Contains("ACTIVE_A", Payloads(request));
                    Assert.DoesNotContain("INACTIVE_B", Payloads(request));
                    Assert.DoesNotContain(
                        request.Tools,
                        tool => tool.Name == "skill.helper");
                    return FinalEvents(request);
                }),
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "dynamic-skill-test-model",
                MaxConcurrentProviderCalls = 1,
                SkillDisclosureBudget = new SkillDisclosureBudget(
                    maxActivatedSkills: 2,
                    maxPromptFragments: 2,
                    maxPromptUtf8Bytes: 12,
                    maxReferences: 8)
            });
        proposedDigest = AssertSkill(rig, "new-skill").ContentDigest;
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = run,
                ActiveSkills = new[]
                {
                    new SkillReference("already-active", "1.0.0")
                }
            });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(2, rig.Provider.Requests.Count);
        Assert.Single(
            outcome.Transcript
                .Where(message => message.Role == NormalizedRoles.Tool)
                .SelectMany(message => message.Parts)
                .Where(part => part.Json.HasValue)
                .Select(part => part.Json!.Value),
            value => value.TryGetProperty("reasonCode", out var reason)
                     && reason.GetString()
                     == "skill_prompt_bytes_exceeded");

        var events = await rig.Store.ReadRunAsync(run.RunId, default);
        Assert.DoesNotContain(
            events,
            item => item.Kind == RuntimeEventKinds.RunCheckpoint
                    && item.EventId.StartsWith(
                        "skill-activation-checkpoint:",
                        StringComparison.Ordinal));
        var latestSnapshot = events.Last(
            item => item.Kind == RuntimeEventKinds.TurnSnapshot);
        var activations = latestSnapshot.Payload
            .GetProperty("extensions")
            .GetProperty(SkillActivationStateCodec.ExtensionName)
            .GetProperty("activations")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            "already-active",
            Assert.Single(activations).GetProperty("skillId").GetString());
    }

    [Fact]
    public async Task DynamicActivationSurvivesDurableResume()
    {
        var manifest = Skill(
            "resume-dialogue",
            "Resume dialogue after an uncertain game operation.",
            "RESUMED_DYNAMIC_SKILL_MARKER");
        var pauseTool = Tool(
            "pause_world",
            ToolEffects.ExternalWrite);
        string skillDigest = string.Empty;
        await using var rig = new RuntimeRig(
            new[] { pauseTool },
            new[] { manifest },
            skills => new ScriptedProvider(
                request => ToolCalls(
                    request,
                    new ToolCallSpec(
                        "activate-resume-skill",
                        SkillRuntimeControlNames.Activate,
                        ActivationArguments(
                            "resume-dialogue",
                            skillDigest))),
                request =>
                {
                    Assert.Contains(
                        "RESUMED_DYNAMIC_SKILL_MARKER",
                        Payloads(request));
                    return ToolCalls(
                        request,
                        new ToolCallSpec(
                            "pause-world",
                            "pause_world",
                            "{}"));
                },
                request =>
                {
                    Assert.Contains(
                        "RESUMED_DYNAMIC_SKILL_MARKER",
                        Payloads(request));
                    return FinalEvents(request);
                }),
            host: new UnknownReceiptHost());
        skillDigest = AssertSkill(rig, "resume-dialogue").ContentDigest;
        var run = Run();

        var first = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = run });

        Assert.Equal(RunStates.Reconciling, first.Run.State);
        var resumed = await rig.Runtime.ResumeAsync(
            run.RunId,
            reconciler: new SucceedingReconciler());

        Assert.Equal(RunStates.Completed, resumed.Run.State);
        Assert.Equal(3, rig.Provider.Requests.Count);
        Assert.Contains(
            "RESUMED_DYNAMIC_SKILL_MARKER",
            Payloads(rig.Provider.Requests[^1]));
    }

    [Fact]
    public async Task ExactInitialSkillStateRecoversBeforeFirstTurnSnapshot()
    {
        var manifest = Skill(
            "initial-recovery",
            "Recovers exact initial state.",
            "INITIAL_RECOVERY_MARKER");
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents));
        var skill = AssertSkill(rig, "initial-recovery");
        var run = Run();
        var references = new[]
        {
            new SkillReference(skill.SkillId, skill.Version)
        };
        SkillActivationStateCodec.Attach(
            run,
            new[]
            {
                new SkillActivationStateRecord(
                    skill.SkillId,
                    skill.Version,
                    skill.ContentDigest)
            });

        await rig.CommitRunStartOnlyAsync(run, references);
        var recovered = await rig.Recovery.LoadAsync(
            run.RunId,
            CancellationToken.None);

        var actual = Assert.Single(
            Assert.IsType<RecoveredRun>(recovered)
                .RecoverySkillActivationState);
        Assert.Equal(skill.SkillId, actual.SkillId);
        Assert.Equal(skill.Version, actual.Version);
        Assert.Equal(skill.ContentDigest, actual.ContentDigest);
    }

    [Fact]
    public async Task ResolvedSkillContentIsBoundedAndInjectedAsNonAuthoritativeContext()
    {
        const string secretContextReference =
            @"C:\private\skills\npc-context.json";
        const string secretToken = "TOP_SECRET_RUNTIME_TOKEN";
        const string secretResourceReference =
            "https://internal.example/skills/rules"
            + "?token=TOP_SECRET_RUNTIME_TOKEN";
        const string secretRelatedReference =
            "https://internal.example/skills/child"
            + "?token=TOP_SECRET_RUNTIME_TOKEN";
        const string secretRootMediaToken =
            "TOP_SECRET_MANIFEST_MEDIA_TOKEN";
        const string secretRootMediaType =
            "application/json; profile=TOP_SECRET_MANIFEST_MEDIA_TOKEN";
        const string secretRelatedMediaToken =
            "TOP_SECRET_RELATED_MEDIA_TOKEN";
        const string secretRelatedMediaType =
            @"C:\private\TOP_SECRET_RELATED_MEDIA_TOKEN";
        var rulesContent = Json("\"rules\"");
        var rulesDigest = CanonicalJsonDigest.ComputeSha256(rulesContent);
        var manifest = Skill(
            "world-context",
            "Use bounded world context.",
            "CONTEXT_SKILL_MARKER",
            contextProviders: new[] { secretContextReference },
            resources: new[]
            {
                new ResourceReference
                {
                    Uri = secretResourceReference,
                    MediaType = secretRootMediaType,
                    Digest = "sha256:" + rulesDigest,
                    SizeBytes = 7
                }
            });
        var resolver = new RecordingResolver(
            request =>
            {
                if (request.Reference.Kind
                    == SkillContentReferenceKinds.ContextProvider)
                {
                    return new SkillContentResolution(
                        Json("""{"mood":"alert","turn":12}"""),
                        new[]
                        {
                            SkillContentReference.Resource(
                                secretRelatedReference,
                                secretRelatedMediaType)
                        });
                }

                if (request.Reference.Reference == secretResourceReference)
                {
                    return new SkillContentResolution(
                        rulesContent,
                        digest: rulesDigest,
                        sizeBytes: 7);
                }

                return new SkillContentResolution(
                    Json("""{"child":"resolved"}"""));
            });
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents),
            resolver: resolver,
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "dynamic-skill-test-model",
                MaxConcurrentProviderCalls = 1,
                SkillRuntimeLimits = new SkillRuntimeLimits(
                    maxResolvedItems: 8,
                    maxResolvedItemUtf8Bytes: 512,
                    maxResolvedUtf8Bytes: 1_024,
                    maxReferenceDepth: 2)
            });
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            Request(run, "world-context"));

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(3, resolver.Requests.Count);
        Assert.Contains(
            resolver.Requests,
            request => request.Reference.Reference
                       == secretContextReference);
        Assert.Contains(
            resolver.Requests,
            request => request.Reference.Reference
                       == secretResourceReference);
        Assert.Contains(
            resolver.Requests,
            request => request.Reference.Reference
                       == secretRelatedReference);
        var request = Assert.Single(rig.Provider.Requests);
        var providerPayloads = Payloads(request);
        Assert.DoesNotContain(
            secretContextReference,
            providerPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "internal.example",
            providerPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretToken,
            providerPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretRootMediaToken,
            providerPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretRelatedMediaToken,
            providerPayloads,
            StringComparison.Ordinal);
        var skillContentParts =
            request.Messages
                .Where(message => message.Role == NormalizedRoles.User)
                .SelectMany(message => message.Parts)
                .Where(
                    part => part.Json.HasValue
                            && part.Json.Value.TryGetProperty(
                                "contentType",
                                out var contentType)
                            && contentType.GetString()
                            == "application/vnd.game-agent."
                               + "skill-content+json");
        var skillContent = Assert.Single(skillContentParts.ToArray());
        var payload = skillContent.Json!.Value;
        Assert.Equal(
            "non_authoritative",
            payload.GetProperty("authority").GetString());
        Assert.Equal("context_only", payload.GetProperty("usage").GetString());
        var items = payload.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.All(
            items,
            item =>
            {
                Assert.Equal(
                    "non_authoritative",
                    item.GetProperty("authority").GetString());
                Assert.True(
                    CanonicalJsonDigest.IsSha256(
                        item.GetProperty("contentDigest").GetString()));
                Assert.True(
                    CanonicalJsonDigest.IsSha256(
                        item.GetProperty("referenceDigest").GetString()));
                Assert.False(item.TryGetProperty("reference", out _));
            });
        Assert.Single(
            items,
            item => item.TryGetProperty("mediaType", out var mediaType)
                    && mediaType.GetString() == "application/json");
        Assert.DoesNotContain(
            items,
            item => item.TryGetProperty("mediaType", out var mediaType)
                    && mediaType.GetString() == secretRelatedMediaType);
        Assert.Contains(
            items,
            item => item.GetProperty("status").GetString() == "resolved"
                    && item.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Object
                    && content.GetProperty("mood").GetString() == "alert");
        var snapshot = Assert.Single(
            await rig.Store.ReadRunAsync(run.RunId, default),
            item => item.Kind == RuntimeEventKinds.TurnSnapshot);
        var evidence = snapshot.Payload
            .GetProperty("extensions")
            .GetProperty("skillContentResolution");
        Assert.Equal(3, evidence.GetProperty("resolvedCount").GetInt32());
        Assert.Equal(0, evidence.GetProperty("failedCount").GetInt32());
        Assert.Contains(
            evidence.GetProperty("reasonCodes")
                .EnumerateArray()
                .Select(value => value.GetString()),
            value => value == SkillRuntimeReasonCodes.Resolved);
        var journalPayloads = string.Join(
            "\n",
            (await rig.Store.ReadRunAsync(run.RunId, default))
            .Select(item => item.Payload.GetRawText()));
        Assert.DoesNotContain(
            secretContextReference,
            journalPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "internal.example",
            journalPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretToken,
            journalPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretRootMediaToken,
            journalPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretRelatedMediaToken,
            journalPayloads,
            StringComparison.Ordinal);
        AssertUtf8BytesDoNotContain(
            Encoding.UTF8.GetBytes(providerPayloads),
            secretRootMediaToken,
            secretRelatedMediaToken);
        AssertUtf8BytesDoNotContain(
            ReadSharedFileBytes(rig.JournalPath),
            secretRootMediaToken,
            secretRelatedMediaToken);
    }

    [Fact]
    public async Task ResolverFailureDoesNotLeakReferenceOrHostError()
    {
        const string secretReference =
            "https://internal.example/context"
            + "?access_token=DO_NOT_PERSIST_THIS_TOKEN";
        const string secretToken = "DO_NOT_PERSIST_THIS_TOKEN";
        var manifest = Skill(
            "private-context",
            "Requires private host context.",
            "PRIVATE_CONTEXT_MARKER",
            contextProviders: new[] { secretReference });
        var resolver = new RecordingResolver(
            request => throw new InvalidOperationException(
                "Could not read "
                + request.Reference.Reference
                + " using "
                + secretToken));
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents),
            resolver: resolver);
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            Request(run, "private-context"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillRuntimeReasonCodes.ResolverError,
            outcome.ErrorCode);
        Assert.DoesNotContain(
            secretReference,
            outcome.SafeErrorMessage ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretToken,
            outcome.SafeErrorMessage ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Empty(rig.Provider.Requests);
        Assert.Single(resolver.Requests);
        var journalPayloads = string.Join(
            "\n",
            (await rig.Store.ReadRunAsync(run.RunId, default))
            .Select(item => item.Payload.GetRawText()));
        Assert.DoesNotContain(
            secretReference,
            journalPayloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretToken,
            journalPayloads,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntrustedOrUnsatisfiedSkillsCannotExposeActivationControls()
    {
        var manifests = new[]
        {
            Skill(
                "untrusted-skill",
                "Must remain hidden.",
                "UNTRUSTED_MARKER",
                trust: "untrusted"),
            Skill(
                "missing-tool-skill",
                "Requires an unavailable game tool.",
                "MISSING_TOOL_MARKER",
                requiredTools: new[] { "world.lookup@1.0.0" })
        };
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            manifests,
            _ => new ScriptedProvider(
                request =>
                {
                    Assert.DoesNotContain(
                        request.Tools,
                        tool => SkillRuntimeControlNames.IsReserved(
                            tool.Name));
                    var payload = Payloads(request);
                    Assert.DoesNotContain("untrusted-skill", payload);
                    Assert.DoesNotContain("missing-tool-skill", payload);
                    return FinalEvents(request);
                }));

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
    }

    [Fact]
    public async Task OversizedResolverResultIsObservableAndNeverInjected()
    {
        var manifest = Skill(
            "bounded-content",
            "Bound resolved content.",
            "BOUNDED_CONTENT_MARKER",
            contextProviders: new[] { "large-context" });
        var resolver = new RecordingResolver(
            _ => new SkillContentResolution(
                Json("\"" + new string('x', 512) + "\"")));
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents),
            resolver: resolver,
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "dynamic-skill-test-model",
                MaxConcurrentProviderCalls = 1,
                SkillRuntimeLimits = new SkillRuntimeLimits(
                    maxResolvedItemUtf8Bytes: 64,
                    maxResolvedUtf8Bytes: 128)
            });

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "bounded-content"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillRuntimeReasonCodes.ItemLimitExceeded,
            outcome.ErrorCode);
        Assert.Equal("skill_content", outcome.ErrorCategory);
        Assert.Empty(rig.Provider.Requests);
    }

    [Fact]
    public async Task DeclaredContentWithoutResolverFailsClosedBeforeProvider()
    {
        var manifest = Skill(
            "resolver-required",
            "Requires host context.",
            "RESOLVER_REQUIRED_MARKER",
            contextProviders: new[] { "required-context" });
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents));

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "resolver-required"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillRuntimeReasonCodes.ResolverUnavailable,
            outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
    }

    [Fact]
    public async Task DeclaredDigestIsVerifiedAgainstCanonicalResolvedContent()
    {
        var manifest = Skill(
            "digest-bound",
            "Requires digest-bound content.",
            "DIGEST_BOUND_MARKER",
            resources: new[]
            {
                new ResourceReference
                {
                    Uri = "skill://digest-bound",
                    MediaType = "application/json",
                    Digest = "sha256:"
                             + new string('0', 64)
                }
            });
        var resolver = new RecordingResolver(
            _ => new SkillContentResolution(
                Json("""{"actual":"different"}"""),
                digest: new string('0', 64)));
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents),
            resolver: resolver);

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "digest-bound"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillRuntimeReasonCodes.DigestMismatch,
            outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
    }

    [Fact]
    public async Task NonCooperativeResolverTimesOutWithoutBlockingRunOrShutdown()
    {
        var manifest = Skill(
            "timeout-bound",
            "Requires a bounded resolver.",
            "TIMEOUT_BOUND_MARKER",
            contextProviders: new[] { "never-completes" });
        var resolver = new HangingResolver();
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents),
            resolver: resolver,
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "dynamic-skill-test-model",
                MaxConcurrentProviderCalls = 1,
                SkillRuntimeLimits = new SkillRuntimeLimits(
                    resolverTimeoutMilliseconds: 30,
                    maxConcurrentResolverCalls: 1)
            });
        var started = DateTimeOffset.UtcNow;

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "timeout-bound"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillRuntimeReasonCodes.ResolverTimeout,
            outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
        Assert.True(
            DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
        await resolver.CancellationRequested.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.Equal(1, rig.Runtime.DetachedSkillContentResolverCallCount);
        resolver.Release();
        await WaitUntilAsync(
            () => rig.Runtime.DetachedSkillContentResolverCallCount == 0);
    }

    [Fact]
    public async Task ShutdownCancelsAndBoundsResolverThatIgnoresCancellation()
    {
        var manifest = Skill(
            "shutdown-bound",
            "Requires a resolver that ignores cancellation.",
            "SHUTDOWN_BOUND_MARKER",
            contextProviders: new[] { "held-open" });
        var resolver = new HangingResolver();
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents),
            resolver: resolver,
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "dynamic-skill-test-model",
                MaxConcurrentProviderCalls = 1,
                ShutdownDrainTimeout = TimeSpan.FromMilliseconds(500),
                SkillRuntimeLimits = new SkillRuntimeLimits(
                    resolverTimeoutMilliseconds: 100,
                    maxConcurrentResolverCalls: 1)
            });
        var run = rig.Runtime.RunAsync(
                Request(Run(), "shutdown-bound"))
            .AsTask();
        await resolver.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var started = DateTimeOffset.UtcNow;

        await rig.Runtime.StopAsync();

        Assert.True(
            DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
        await resolver.CancellationRequested.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.False(rig.Runtime.SkillContentResolversDrainedOnStop);
        Assert.Equal(1, rig.Runtime.DetachedSkillContentResolverCallCount);
        _ = await run.WaitAsync(TimeSpan.FromSeconds(2));
        resolver.Release();
        await WaitUntilAsync(
            () => rig.Runtime.DetachedSkillContentResolverCallCount == 0);
    }

    [Fact]
    public async Task BlockingResolverCancellationRemainsBoundedAndTracked()
    {
        var manifest = Skill(
            "blocking-cancellation",
            "Exercises cancellation isolation.",
            "BLOCKING_CANCELLATION_MARKER",
            contextProviders: new[] { "blocking-callback" });
        var resolver = new BlockingCancellationResolver();
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents),
            resolver: resolver,
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "dynamic-skill-test-model",
                MaxConcurrentProviderCalls = 1,
                ShutdownDrainTimeout = TimeSpan.FromMilliseconds(500),
                SkillRuntimeLimits = new SkillRuntimeLimits(
                    resolverTimeoutMilliseconds: 30,
                    maxConcurrentResolverCalls: 1)
            });

        try
        {
            var outcome = await rig.Runtime.RunAsync(
                Request(Run(), "blocking-cancellation"));

            Assert.Equal(RunStates.Failed, outcome.Run.State);
            Assert.Equal(
                SkillRuntimeReasonCodes.ResolverTimeout,
                outcome.ErrorCode);
            await resolver.CallbackStarted.WaitAsync(
                TimeSpan.FromSeconds(2));
            await resolver.ResolverCompleted.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.Equal(
                1,
                rig.Runtime.DetachedSkillContentResolverCallCount);

            var started = DateTimeOffset.UtcNow;
            await rig.Runtime.WaitForShutdownDrainAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(
                DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
            Assert.False(rig.Runtime.SkillContentResolversDrainedOnStop);
            Assert.Equal(
                1,
                rig.Runtime.DetachedSkillContentResolverCallCount);
        }
        finally
        {
            resolver.ReleaseCallback();
        }

        await WaitUntilAsync(
            () => rig.Runtime.DetachedSkillContentResolverCallCount == 0);
    }

    [Fact]
    public async Task RelatedReferenceFanOutIsRejectedBeforeTraversal()
    {
        var manifest = Skill(
            "fan-out-bound",
            "Returns too many related references.",
            "FAN_OUT_BOUND_MARKER",
            contextProviders: new[] { "root" });
        var resolver = new RecordingResolver(
            _ => new SkillContentResolution(
                Json("""{"root":true}"""),
                Enumerable.Range(0, 4)
                    .Select(
                        index => SkillContentReference.ContextProvider(
                            "child-" + index))
                    .ToArray()));
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { manifest },
            _ => new ScriptedProvider(FinalEvents),
            resolver: resolver,
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "dynamic-skill-test-model",
                MaxConcurrentProviderCalls = 1,
                SkillRuntimeLimits = new SkillRuntimeLimits(
                    maxResolvedItems: 3)
            });

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "fan-out-bound"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillRuntimeReasonCodes.ReferenceCountExceeded,
            outcome.ErrorCode);
        Assert.Single(resolver.Requests);
        Assert.Empty(rig.Provider.Requests);
    }

    [Fact]
    public void ResolverReportedDigestIsBoundedAtConstruction()
    {
        Assert.Throws<RuntimeContentLimitException>(
            () => new SkillContentResolution(
                Json("{}"),
                digest: new string('a', 72)));
    }

    [Fact]
    public void ReferenceDigestBindsTheCompleteResolverIdentity()
    {
        const string sharedReference =
            "https://internal.example/shared?token=PRIVATE";
        var registry = new SkillCatalogRegistry();
        registry.Replace(
            new[]
            {
                Skill(
                "reference-identity",
                "Tests reference identity.",
                    "REFERENCE_IDENTITY_MARKER")
            });
        Assert.True(
            registry.Current.TryGet(
                "reference-identity",
                "1.0.0",
                out var skill));
        var references = new[]
        {
            SkillContentReference.ContextProvider(sharedReference),
            SkillContentReference.Resource(
                sharedReference,
                "application/json"),
            SkillContentReference.Resource(
                sharedReference,
                "text/plain"),
            SkillContentReference.Resource(
                sharedReference,
                "application/json",
                digest: "sha256:" + new string('0', 64)),
            SkillContentReference.Resource(
                sharedReference,
                "application/json",
                sizeBytes: 1)
        };
        var selection = new SkillContentResolutionSelection(
            references
                .Select(
                    reference => new SkillContentResolvedItem(
                        skill!,
                        reference,
                        depth: 0,
                        status: "resolved",
                        reasonCode: SkillRuntimeReasonCodes.Resolved,
                        content: Json("{}"),
                        contentDigest:
                            CanonicalJsonDigest.ComputeSha256(Json("{}")),
                        contentUtf8Bytes: 2))
                .ToArray(),
            resolvedUtf8Bytes: references.Length * 2,
            truncated: false,
            new[] { SkillRuntimeReasonCodes.Resolved });

        var evidence = selection.Evidence.GetProperty("items")
            .EnumerateArray()
            .ToArray();
        var digests = evidence
            .Select(item => item.GetProperty("referenceDigest").GetString())
            .ToArray();
        Assert.All(
            digests,
            digest => Assert.True(CanonicalJsonDigest.IsSha256(digest)));
        Assert.Equal(
            references.Length,
            digests.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(
            sharedReference,
            selection.Evidence.GetRawText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SkillSearchRejectsTokenAndComparisonBudgetOverruns()
    {
        var manifests = new[]
        {
            Skill(
                "alpha-skill",
                "Find alpha concepts.",
                "ALPHA_SEARCH_MARKER"),
            Skill(
                "beta-skill",
                "Find beta concepts.",
                "BETA_SEARCH_MARKER")
        };
        var tokenLimits = new SkillRuntimeLimits(maxSearchTokens: 2);
        var tokenArguments = Json(
            """{"query":"alpha beta gamma","limit":2}""");

        Assert.False(
            SkillRuntimePlan.TryReadSearch(
                tokenArguments,
                tokenLimits,
                out _,
                out _,
                out var reasonCode));
        Assert.Equal(
            SkillRuntimeReasonCodes.SearchBudgetExceeded,
            reasonCode);

        var comparisonLimits = new SkillRuntimeLimits(
            maxSearchTokens: 8,
            maxSearchComparisons: 5);
        var comparisonPlan = CreateSkillRuntimePlan(
            manifests,
            comparisonLimits);
        var comparisonArguments = Json(
            """{"query":"alpha","limit":2}""");
        Assert.True(
            SkillRuntimePlan.TryReadSearch(
                comparisonArguments,
                comparisonLimits,
                out var query,
                out var limit,
                out reasonCode));

        Assert.False(
            comparisonPlan.TrySearch(
                query,
                limit,
                out var hits,
                out reasonCode));
        Assert.Empty(hits);
        Assert.Equal(
            SkillRuntimeReasonCodes.SearchBudgetExceeded,
            reasonCode);
    }

    [Fact]
    public void StructuredSkillSearchHandlesBmpSupplementaryJapaneseAndKorean()
    {
        var supplementaryCjk = char.ConvertFromUtf32(0x20000);
        var plan = CreateSkillRuntimePlan(
            new[]
            {
                Skill(
                    "bmp-cjk",
                    "龙虎机器策略",
                    "BMP_CJK_MARKER"),
                Skill(
                    "supplementary-cjk",
                    supplementaryCjk + " 遗迹导航",
                    "SUPPLEMENTARY_CJK_MARKER"),
                Skill(
                    "japanese",
                    "カタカナ案内",
                    "JAPANESE_MARKER"),
                Skill(
                    "korean",
                    "한글전략",
                    "KOREAN_MARKER")
            },
            new SkillRuntimeLimits());
        var cases = new[]
        {
            ("zh-Hans", "龙虎", "bmp-cjk"),
            ("zh-Hant", supplementaryCjk, "supplementary-cjk"),
            ("ja-JP", "カタカナ", "japanese"),
            ("ko-KR", "한글전략", "korean")
        };

        foreach (var (locale, term, expectedSkillId) in cases)
        {
            var arguments = Json(
                JsonSerializer.Serialize(
                    new
                    {
                        query = new
                        {
                            locale,
                            terms = new[] { term }
                        },
                        limit = 8
                    }));
            Assert.True(
                SkillRuntimePlan.TryReadSearch(
                    arguments,
                    plan.Limits,
                    out var query,
                    out var limit,
                    out var reasonCode),
                reasonCode);
            Assert.True(
                plan.TrySearch(
                    query,
                    limit,
                    out var hits,
                    out reasonCode),
                reasonCode);
            Assert.Contains(
                hits,
                hit => hit.Skill.SkillId == expectedSkillId);
        }
    }

    [Fact]
    public void SkillSearchRejectsInvalidDescriptionAndBoundsInvalidQuery()
    {
        const string unpairedHigh = "\ud800";
        Assert.Throws<ArgumentException>(
            () => CreateSkillRuntimePlan(
                new[]
                {
                    Skill(
                        "invalid-unicode-description",
                        "before" + unpairedHigh + "after",
                        "INVALID_UNICODE_MARKER")
                },
                new SkillRuntimeLimits()));
        var plan = CreateSkillRuntimePlan(
            new[]
            {
                Skill(
                    "valid-unicode-description",
                    "before after stable",
                    "VALID_UNICODE_MARKER")
            },
            new SkillRuntimeLimits());
        var malformedHighArguments = Json(
            """{"query":"\ud800","limit":1}""");
        var malformedLowArguments = Json(
            """{"query":"\udfff","limit":1}""");

        Assert.False(
            SkillRuntimePlan.TryReadSearch(
                malformedHighArguments,
                plan.Limits,
                out _,
                out _,
                out var reasonCode));
        Assert.Equal(
            SkillRuntimeReasonCodes.SearchArgumentsInvalid,
            reasonCode);
        Assert.False(
            SkillRuntimePlan.TryReadSearch(
                malformedLowArguments,
                plan.Limits,
                out _,
                out _,
                out reasonCode));
        Assert.Equal(
            SkillRuntimeReasonCodes.SearchArgumentsInvalid,
            reasonCode);

        var validArguments = Json(
            """{"query":"after stable","limit":1}""");
        Assert.True(
            SkillRuntimePlan.TryReadSearch(
                validArguments,
                plan.Limits,
                out var query,
                out var limit,
                out reasonCode),
            reasonCode);
        Assert.True(
            plan.TrySearch(
                query,
                limit,
                out var hits,
                out reasonCode),
            reasonCode);
        Assert.Contains(
            hits,
            hit => hit.Skill.SkillId == "valid-unicode-description");
    }

    [Fact]
    public async Task ResolverCancellationCapacityIsIsolatedAndSharedAcrossRuntimes()
    {
        Assert.NotSame(
            BoundedCancellationDispatcher.Shared,
            BoundedCancellationDispatcher.SkillContentResolverShared);
        Assert.NotSame(
            BoundedCancellationDispatcher.LifecycleShared,
            BoundedCancellationDispatcher.SkillContentResolverShared);

        var registry = new SkillCatalogRegistry();
        registry.Replace(
            new[]
            {
                Skill(
                    "dispatcher-bound",
                    "Exercises shared resolver cancellation capacity.",
                    "DISPATCHER_BOUND_MARKER",
                    contextProviders: new[] { "held-reference" })
            });
        var skill = Assert.Single(registry.Current.Skills);
        var limits = new SkillRuntimeLimits(
            resolverTimeoutMilliseconds: 50,
            maxConcurrentResolverCalls: 1);
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        var firstResolver = new CoordinatedCancellationResolver();
        var secondResolver = new CoordinatedCancellationResolver();
        var firstRuntime = new SkillContentRuntime(
            firstResolver,
            limits,
            dispatcher);
        var secondRuntime = new SkillContentRuntime(
            secondResolver,
            limits,
            dispatcher);

        try
        {
            var firstResolution = firstRuntime.ResolveAsync(
                    Run(),
                    "turn-dispatcher-first",
                    new[] { skill },
                    CancellationToken.None)
                .AsTask();
            await firstResolver.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var firstFailure =
                await Assert.ThrowsAsync<SkillContentResolutionException>(
                    () => firstResolution);
            Assert.Equal(
                SkillRuntimeReasonCodes.ResolverTimeout,
                firstFailure.ReasonCode);
            await firstResolver.CallbackStarted.WaitAsync(
                TimeSpan.FromSeconds(2));
            await firstResolver.ResolverCompleted.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.Equal(1, dispatcher.ActiveReservations);
            Assert.Equal(1, firstRuntime.DetachedResolverCallCount);

            var secondResolution = secondRuntime.ResolveAsync(
                    Run(),
                    "turn-dispatcher-second",
                    new[] { skill },
                    CancellationToken.None)
                .AsTask();
            await secondResolver.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var secondFailure =
                await Assert.ThrowsAsync<SkillContentResolutionException>(
                    () => secondResolution);
            Assert.Equal(
                SkillRuntimeReasonCodes.ResolverTimeout,
                secondFailure.ReasonCode);
            Assert.Equal(0, secondResolver.CancellationCallbackCount);
            Assert.Equal(1, secondRuntime.DetachedResolverCallCount);
            Assert.Equal(
                2,
                firstRuntime.DetachedResolverCallCount
                + secondRuntime.DetachedResolverCallCount);
            Assert.Equal(1, dispatcher.ActiveReservations);

            Assert.False(await firstRuntime.StopAsync());
            Assert.False(await secondRuntime.StopAsync());
            Assert.Equal(1, dispatcher.ActiveReservations);
        }
        finally
        {
            secondResolver.Complete();
            secondResolver.ReleaseCallback();
            firstResolver.Complete();
            firstResolver.ReleaseCallback();
            await WaitUntilAsync(
                () => firstRuntime.DetachedResolverCallCount == 0
                      && secondRuntime.DetachedResolverCallCount == 0
                      && dispatcher.ActiveReservations == 0);
        }
    }

    [Fact]
    public async Task ResolverCallbackCapacityFailsWithStableReasonAndRecovers()
    {
        var limiter = new BoundedCallbackProcessLimiter(1);
        var resolverDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var blockerDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var registry = new SkillCatalogRegistry();
        registry.Replace(
            new[]
            {
                Skill(
                    "callback-capacity",
                    "Tests callback admission.",
                    "CALLBACK_CAPACITY_MARKER",
                    contextProviders: new[] { "root" })
            });
        var skill = Assert.Single(registry.Current.Skills);
        var resolver = new RecordingResolver(
            _ => new SkillContentResolution(Json("{\"ok\":true}")));
        var runtime = new SkillContentRuntime(
            resolver,
            new SkillRuntimeLimits(),
            new BoundedCancellationDispatcher(),
            resolverDispatcher);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(
            blockerDispatcher.TryExecute(
                () =>
                {
                    entered.TrySetResult(true);
                    release.Wait();
                    return new ValueTask<int>(1);
                },
                out var blocker));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var error = await Assert.ThrowsAsync<SkillContentResolutionException>(
                () => runtime.ResolveAsync(
                        Run(),
                        "callback-capacity-turn",
                        new[] { skill },
                        CancellationToken.None)
                    .AsTask());
            Assert.Equal(
                SkillRuntimeReasonCodes.ResolverCapacityExceeded,
                error.ReasonCode);
            Assert.Empty(resolver.Requests);
        }
        finally
        {
            release.Set();
            await blocker.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var recovered = await runtime.ResolveAsync(
            Run(),
            "callback-capacity-recovered",
            new[] { skill },
            CancellationToken.None);
        Assert.Single(recovered.Items);
        Assert.Single(resolver.Requests);
        Assert.True(await runtime.StopAsync());
    }

    private static SkillCatalogEntry AssertSkill(
        RuntimeRig rig,
        string id)
    {
        Assert.True(
            rig.Skills.Current.TryGet(id, "1.0.0", out var skill));
        return Assert.IsType<SkillCatalogEntry>(skill);
    }

    private static SkillRuntimePlan CreateSkillRuntimePlan(
        IReadOnlyList<SkillManifest> manifests,
        SkillRuntimeLimits limits)
    {
        var registry = new SkillCatalogRegistry();
        registry.Replace(manifests);
        var snapshot = registry.Current;
        return new SkillRuntimePlan(
            snapshot,
            snapshot.Skills.Select(value => value.Reference).ToArray(),
            Array.Empty<SkillActivationStateRecord>(),
            limits);
    }

    private static void AssertSkillControls(StreamingModelRequest request)
    {
        Assert.Contains(
            request.Tools,
            tool => tool.Name == SkillRuntimeControlNames.Search);
        Assert.Contains(
            request.Tools,
            tool => tool.Name == SkillRuntimeControlNames.Activate);
    }

    private static DurableRunRequest Request(AgentRun run, string skillId) =>
        new()
        {
            Run = run,
            ActiveSkills = new[]
            {
                new SkillReference(skillId, "1.0.0")
            }
        };

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
                MaxTokens = 8_000,
                MaxActions = 8,
                MaxCostUsd = "1"
            },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static SkillManifest Skill(
        string id,
        string description,
        string prompt,
        string trust = "trusted",
        IReadOnlyList<string>? requiredTools = null,
        IReadOnlyList<string>? contextProviders = null,
        IReadOnlyList<ResourceReference>? resources = null)
    {
        return new SkillManifest
        {
            SkillId = id,
            Version = "1.0.0",
            Digest = "declared:" + id,
            Description = description,
            PromptFragments = new List<string> { prompt },
            RequiredToolRefs = requiredTools?.ToList() ?? new List<string>(),
            ContextProviderRefs =
                contextProviders?.ToList() ?? new List<string>(),
            ResourceRefs = resources?.ToList() ?? new List<ResourceReference>(),
            CapabilityRequirements = Json("{}"),
            ActivationPolicy = Json("{}"),
            Trust = trust
        };
    }

    private static ToolDescriptor Tool(string name, string effect)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1.0.0",
            Description = "Test game tool.",
            ParametersSchema =
                Json("""{"type":"object","additionalProperties":false}"""),
            Effect = effect,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 2_000,
            RetryPolicy = ToolRetryPolicies.Never,
            IdempotencyPolicy = ToolIdempotencyPolicies.Required,
            Toolset = "world",
            Visibility = ToolVisibilities.Direct
        };
    }

    private static string ActivationArguments(
        string skillId,
        string skillDigest)
    {
        return JsonSerializer.Serialize(
            new
            {
                skillId,
                version = "1.0.0",
                skillDigest
            });
    }

    private static string Payloads(StreamingModelRequest request)
    {
        return string.Join(
            "\n",
            request.Messages
                .SelectMany(message => message.Parts)
                .Where(part => part.Json.HasValue)
                .Select(part => part.Json!.Value.GetRawText()));
    }

    private static IEnumerable<ModelStreamEvent> ToolCalls(
        StreamingModelRequest request,
        params ToolCallSpec[] calls)
    {
        var events = new List<ModelStreamEvent>();
        long ordinal = 0;
        foreach (var call in calls)
        {
            events.Add(
                new ModelStreamEvent
                {
                    StreamAttemptId = request.StreamAttemptId,
                    Ordinal = ordinal++,
                    Kind = ModelStreamEventKinds.ToolCallDelta,
                    ToolCallId = call.CallId,
                    ToolNameDelta = call.Name,
                    ArgumentsJsonDelta = call.Arguments
                });
        }

        events.Add(
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = ordinal++,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                    CostUsd = "0"
                }
            });
        events.Add(
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = ordinal,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "tool_calls"
            });
        return events;
    }

    private static IEnumerable<ModelStreamEvent> FinalEvents(
        StreamingModelRequest request)
    {
        return new[]
        {
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.TextDelta,
                TextDelta = "\"ok\""
            },
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                    CostUsd = "0"
                }
            },
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "stop"
            }
        };
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static void AssertUtf8BytesDoNotContain(
        byte[] payload,
        params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.True(
                payload.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0,
                $"The UTF-8 payload contained forbidden token '{secret}'.");
        }
    }

    private static byte[] ReadSharedFileBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "The expected asynchronous state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed record ToolCallSpec(
        string CallId,
        string Name,
        string Arguments);

    private sealed class RuntimeRig : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _journalPath;
        private readonly JournalCoordinator _journal;

        public RuntimeRig(
            IReadOnlyList<ToolDescriptor> tools,
            IReadOnlyList<SkillManifest> skills,
            Func<SkillCatalogRegistry, ScriptedProvider> providerFactory,
            IGameHost? host = null,
            ISkillContentResolver? resolver = null,
            DurableAgentRuntimeOptions? options = null)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "game-agent-dynamic-skill-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _journalPath = Path.Combine(_directory, "runtime.journal");
            Store = new FileSessionStore(_journalPath);
            Tools = new ToolCatalogRegistry();
            Tools.Replace(tools);
            Skills = new SkillCatalogRegistry();
            Skills.Replace(skills);
            Provider = providerFactory(Skills);
            var clock = new SystemRuntimeClock();
            var ids = new GuidRuntimeIdGenerator();
            _journal = new JournalCoordinator(Store, Store, clock, ids);
            Recovery = new RunRecovery(Store, Store, _journal);
            Runtime = new DurableAgentRuntime(
                new ProviderAttemptRunner(
                    new[] { Provider },
                    new ProviderRetryPolicy
                    {
                        MaxAttemptsPerProvider = 1,
                        IdleTimeout = TimeSpan.FromSeconds(2),
                        TotalTimeout = TimeSpan.FromSeconds(5)
                    },
                    new SystemRuntimeDelay(),
                    ids),
                host ?? new RejectingHost(),
                _journal,
                Recovery,
                Tools,
                Skills,
                new ContextCompiler(),
                new ToolBatchPlanner(),
                new ToolBatchScheduler(),
                clock,
                ids,
                options
                ?? new DurableAgentRuntimeOptions
                {
                    ModelId = "dynamic-skill-test-model",
                    MaxConcurrentProviderCalls = 1
                },
                skillContentResolver: resolver);
        }

        public FileSessionStore Store { get; }

        public string JournalPath => _journalPath;

        public ToolCatalogRegistry Tools { get; }

        public SkillCatalogRegistry Skills { get; }

        public ScriptedProvider Provider { get; }

        public RunRecovery Recovery { get; }

        public DurableAgentRuntime Runtime { get; }

        public ValueTask CommitRunStartOnlyAsync(
            AgentRun run,
            IReadOnlyList<SkillReference> activeSkills) =>
            _journal.CommitRunStartAsync(
                run,
                Array.Empty<NormalizedMessage>(),
                Array.Empty<ContextCandidate>(),
                activeSkills,
                ProviderWorkloadClasses.Interactive,
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            _journal.Dispose();
            await Store.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ScriptedProvider : IStreamingModelProvider
    {
        private readonly Queue<
            Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>> _steps;

        public ScriptedProvider(
            params Func<
                StreamingModelRequest,
                IEnumerable<ModelStreamEvent>>[] steps)
        {
            _steps = new Queue<
                Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>>(
                steps);
        }

        public string ProviderId => "dynamic-skill-test-provider";

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
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var events = _steps.Count > 0
                ? _steps.Dequeue()(request)
                : FinalEvents(request);
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingResolver : ISkillContentResolver
    {
        private readonly Func<
            SkillContentResolutionRequest,
            SkillContentResolution> _resolve;

        public RecordingResolver(
            Func<
                SkillContentResolutionRequest,
                SkillContentResolution> resolve)
        {
            _resolve = resolve;
        }

        public List<SkillContentResolutionRequest> Requests { get; } = new();

        public ValueTask<SkillContentResolution> ResolveAsync(
            SkillContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return new ValueTask<SkillContentResolution>(_resolve(request));
        }
    }

    private sealed class HangingResolver : ISkillContentResolver
    {
        private readonly TaskCompletionSource<SkillContentResolution>
            _release = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task CancellationRequested => _cancellationRequested.Task;

        public async ValueTask<SkillContentResolution> ResolveAsync(
            SkillContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            using var cancellationRegistration = cancellationToken.Register(
                () => _cancellationRequested.TrySetResult(true));
            _started.TrySetResult(true);
            return await _release.Task.ConfigureAwait(false);
        }

        public void Release() =>
            _release.TrySetResult(
                new SkillContentResolution(Json("""{"released":true}""")));
    }

    private sealed class BlockingCancellationResolver :
        ISkillContentResolver
    {
        private readonly TaskCompletionSource<SkillContentResolution>
            _result = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _callbackStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _resolverCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseCallback = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CallbackStarted => _callbackStarted.Task;

        public Task ResolverCompleted => _resolverCompleted.Task;

        public async ValueTask<SkillContentResolution> ResolveAsync(
            SkillContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var registration = cancellationToken.Register(
                () =>
                {
                    _result.TrySetResult(
                        new SkillContentResolution(
                            Json("""{"cancelled":true}""")));
                    _callbackStarted.TrySetResult(true);
                    _releaseCallback.Task.GetAwaiter().GetResult();
                });
            var result = await _result.Task.ConfigureAwait(false);
            _resolverCompleted.TrySetResult(true);
            GC.KeepAlive(registration);
            return result;
        }

        public void ReleaseCallback() =>
            _releaseCallback.TrySetResult(true);
    }

    private sealed class CoordinatedCancellationResolver :
        ISkillContentResolver
    {
        private readonly TaskCompletionSource<SkillContentResolution>
            _result = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _callbackStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _resolverCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseCallback = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationCallbackCount;

        public Task Started => _started.Task;

        public Task CallbackStarted => _callbackStarted.Task;

        public Task ResolverCompleted => _resolverCompleted.Task;

        public int CancellationCallbackCount =>
            Volatile.Read(ref _cancellationCallbackCount);

        public async ValueTask<SkillContentResolution> ResolveAsync(
            SkillContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var registration = cancellationToken.Register(
                () =>
                {
                    Interlocked.Increment(
                        ref _cancellationCallbackCount);
                    _result.TrySetResult(
                        new SkillContentResolution(
                            Json("""{"cancelled":true}""")));
                    _callbackStarted.TrySetResult(true);
                    _releaseCallback.Task.GetAwaiter().GetResult();
                });
            _started.TrySetResult(true);
            var result = await _result.Task.ConfigureAwait(false);
            _resolverCompleted.TrySetResult(true);
            GC.KeepAlive(registration);
            return result;
        }

        public void Complete() =>
            _result.TrySetResult(
                new SkillContentResolution(Json("""{"completed":true}""")));

        public void ReleaseCallback() =>
            _releaseCallback.TrySetResult(true);
    }

    private sealed class RejectingHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No action was expected.");
        }
    }

    private sealed class UnknownReceiptHost : IGameHost
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
                    ReceivedAt = DateTimeOffset.UtcNow
                });
        }
    }

    private sealed class SucceedingReconciler : IGameOperationReconciler
    {
        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = Json("""{"reconciled":true}"""),
                    ReceivedAt = now,
                    CommittedAt = now
                });
        }
    }
}
