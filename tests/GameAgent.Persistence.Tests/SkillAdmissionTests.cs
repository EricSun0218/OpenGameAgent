using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class SkillAdmissionTests
{
    [Fact]
    public async Task UntrustedActiveSkillFailsBeforeSystemDisclosureOrProviderDispatch()
    {
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[]
            {
                Skill(
                    "unsafe-skill",
                    trust: "untrusted",
                    prompt: "UNTRUSTED_SYSTEM_MARKER")
            });
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            Request(run, "unsafe-skill"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(SkillAdmissionReasonCodes.Untrusted, outcome.ErrorCode);
        Assert.Equal("skill_admission", outcome.ErrorCategory);
        Assert.Equal(
            SkillAdmissionReasonCodes.Untrusted,
            outcome.Run.TerminalReason);
        Assert.Empty(rig.Provider.Requests);
        Assert.DoesNotContain(
            outcome.Transcript,
            message => string.Equals(
                message.Role,
                NormalizedRoles.System,
                StringComparison.Ordinal));

        var failed = Assert.Single(
            await rig.Store.ReadRunAsync(run.RunId, default),
            item => item.Kind == RuntimeEventKinds.RunFailed);
        Assert.Equal(
            SkillAdmissionReasonCodes.Untrusted,
            failed.Payload.GetProperty("terminalReason").GetString());
    }

    [Theory]
    [InlineData(
        true,
        false,
        SkillAdmissionReasonCodes.CapabilityRequirementsUnsupported)]
    [InlineData(
        false,
        true,
        SkillAdmissionReasonCodes.ActivationPolicyUnsupported)]
    public async Task UnsupportedSkillPolicyMetadataFailsClosed(
        bool hasCapabilities,
        bool hasActivationPolicy,
        string reasonCode)
    {
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[]
            {
                Skill(
                    "declared-skill",
                    capabilities: hasCapabilities
                        ? """{"engine":"any"}"""
                        : "{}",
                    activationPolicy: hasActivationPolicy
                        ? """{"mode":"explicit"}"""
                        : "{}")
            });

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "declared-skill"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(reasonCode, outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
    }

    [Theory]
    [InlineData(
        null,
        SkillAdmissionReasonCodes.RequiredToolMissing)]
    [InlineData(
        "2.0.0",
        SkillAdmissionReasonCodes.RequiredToolVersionMismatch)]
    public async Task RequiredToolMustExistAtTheExactVersion(
        string? availableVersion,
        string reasonCode)
    {
        var tools = availableVersion is null
            ? Array.Empty<ToolDescriptor>()
            : new[] { Tool("world.lookup", availableVersion) };
        await using var rig = new RuntimeRig(
            tools,
            new[]
            {
                Skill(
                    "lookup-skill",
                    requiredTools: new[] { "world.lookup@1.0.0" })
            });

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "lookup-skill"));

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(reasonCode, outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
    }

    [Fact]
    public async Task AllowedDecisionIsPersistedAndUntrustedCatalogContentIsHidden()
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("world.lookup", "1.0.0") },
            new[]
            {
                Skill(
                    "lookup-skill",
                    prompt: "TRUSTED_SKILL_MARKER",
                    requiredTools: new[] { "world.lookup@1.0.0" }),
                Skill(
                    "hidden-skill",
                    trust: "untrusted",
                    prompt: "HIDDEN_UNTRUSTED_MARKER")
            });
        Assert.True(
            rig.Skills.Current.TryGet(
                "lookup-skill",
                "1.0.0",
                out var admittedSkill));
        Assert.NotNull(admittedSkill);
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            Request(run, "lookup-skill"));

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        var request = Assert.Single(rig.Provider.Requests);
        var systemPayload = string.Join(
            "\n",
            request.Messages
                .Where(message => message.Role == NormalizedRoles.System)
                .SelectMany(message => message.Parts)
                .Where(part => part.Json.HasValue)
                .Select(part => part.Json!.Value.GetRawText()));
        Assert.Contains("TRUSTED_SKILL_MARKER", systemPayload);
        Assert.DoesNotContain("HIDDEN_UNTRUSTED_MARKER", systemPayload);
        Assert.DoesNotContain("hidden-skill", systemPayload);

        var turnSnapshotEvent = Assert.Single(
            await rig.Store.ReadRunAsync(run.RunId, default),
            item => item.Kind == RuntimeEventKinds.TurnSnapshot);
        var extensions = turnSnapshotEvent.Payload.GetProperty("extensions");
        var admission = extensions.GetProperty("skillAdmission");
        Assert.Equal(
            DefaultSkillAdmissionPolicy.Instance.PolicyId,
            admission.GetProperty("policyId").GetString());
        Assert.Equal(
            DefaultSkillAdmissionPolicy.Instance.Version,
            admission.GetProperty("policyVersion").GetString());
        var decision = Assert.Single(
            admission.GetProperty("decisions").EnumerateArray());
        Assert.Equal("lookup-skill", decision.GetProperty("skillId").GetString());
        Assert.Equal("1.0.0", decision.GetProperty("skillVersion").GetString());
        Assert.Equal(
            admittedSkill!.ContentDigest,
            decision.GetProperty("skillDigest").GetString());
        Assert.Equal(
            SkillAdmissionReasonCodes.Allowed,
            decision.GetProperty("reasonCode").GetString());
        var contextBudget = extensions.GetProperty("contextBudget");
        Assert.Equal(0, contextBudget.GetProperty("inputCount").GetInt32());
        Assert.Empty(
            contextBudget.GetProperty("selectedIds").EnumerateArray());
        var promptMeasurement = extensions.GetProperty("promptMeasurement");
        Assert.True(promptMeasurement.GetProperty("utf8Bytes").GetInt32() > 0);
        Assert.True(
            promptMeasurement.GetProperty("estimatedTokens").GetInt32() > 0);
    }

    [Fact]
    public async Task RegistryReplacementDuringPolicyEvaluationDoesNotChangeTurnSnapshot()
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("world.lookup", "1.0.0") },
            new[]
            {
                Skill(
                    "lookup-skill",
                    requiredTools: new[] { "world.lookup@1.0.0" })
            },
            tools => new ReplacingAdmissionPolicy(tools));
        var captured = rig.Tools.Current;
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            Request(run, "lookup-skill"));

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.True(rig.Tools.Current.Generation > captured.Generation);
        var providerRequest = Assert.Single(rig.Provider.Requests);
        var providerTool = Assert.Single(providerRequest.Tools);
        Assert.Equal("1.0.0", providerTool.Version);

        var turnSnapshot = Assert.Single(
            await rig.Store.ReadRunAsync(run.RunId, default),
            item => item.Kind == RuntimeEventKinds.TurnSnapshot);
        Assert.Equal(
            captured.Generation,
            turnSnapshot.Payload
                .GetProperty("toolCatalogGeneration")
                .GetInt64());
    }

    [Fact]
    public async Task CustomPolicyCanExplicitlyAdmitDeclarationsTheDefaultDoesNotInterpret()
    {
        var policy = new ExplicitAllowPolicy();
        await using var rig = new RuntimeRig(
            new[] { Tool("world.lookup", "1.0.0") },
            new[]
            {
                Skill(
                    "game-approved-skill",
                    trust: "untrusted",
                    capabilities: """{"engineFeature":"dialogue"}""",
                    activationPolicy: """{"gameRule":"quest-active"}""",
                    requiredTools: new[] { "world.lookup@1.0.0" })
            },
            _ => policy);
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            Request(run, "game-approved-skill"));

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(1, policy.ActivationCalls);
        var turnSnapshot = Assert.Single(
            await rig.Store.ReadRunAsync(run.RunId, default),
            item => item.Kind == RuntimeEventKinds.TurnSnapshot);
        var admission = turnSnapshot.Payload
            .GetProperty("extensions")
            .GetProperty("skillAdmission");
        Assert.Equal(policy.PolicyId, admission.GetProperty("policyId").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(
                admission.GetProperty("admissionDigest").GetString()));
        Assert.Equal(
            "game_skill_approved",
            Assert.Single(admission.GetProperty("decisions").EnumerateArray())
                .GetProperty("reasonCode")
                .GetString());
    }

    [Fact]
    public async Task CustomPolicyCannotBypassRequiredToolInvariant()
    {
        var policy = new ExplicitAllowPolicy();
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[]
            {
                Skill(
                    "missing-tool-skill",
                    trust: "untrusted",
                    capabilities: """{"custom":true}""",
                    activationPolicy: """{"custom":true}""",
                    requiredTools: new[] { "world.lookup@1.0.0" })
            },
            _ => policy);

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "missing-tool-skill"));

        Assert.Equal(
            SkillAdmissionReasonCodes.RequiredToolMissing,
            outcome.ErrorCode);
        Assert.Equal(0, policy.ActivationCalls);
        Assert.Empty(rig.Provider.Requests);
    }

    [Fact]
    public async Task ThrowingCustomPolicyFailsClosedBeforeProviderDispatch()
    {
        await using var rig = new RuntimeRig(
            Array.Empty<ToolDescriptor>(),
            new[] { Skill("policy-error-skill") },
            _ => new ThrowingAdmissionPolicy());

        var outcome = await rig.Runtime.RunAsync(
            Request(Run(), "policy-error-skill"));

        Assert.Equal(SkillAdmissionReasonCodes.PolicyError, outcome.ErrorCode);
        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Empty(rig.Provider.Requests);
    }

    [Fact]
    public async Task InactiveCatalogHotUpdateIsVisibleOnTheNextTurn()
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("advance_turn", "1.0.0") },
            new[]
            {
                Skill(
                    "catalog-skill",
                    prompt: "CATALOG_PROMPT_V1",
                    description: "CATALOG_DESCRIPTION_V1")
            },
            providerFactory: skills => new RecordingProvider(
                request =>
                {
                    skills.Replace(
                        new[]
                        {
                            Skill(
                                "catalog-skill",
                                prompt: "CATALOG_PROMPT_V2",
                                description: "CATALOG_DESCRIPTION_V2")
                        });
                    return ToolCallEvents(
                        request,
                        "catalog-update-call",
                        "advance_turn");
                },
                FinalEvents),
            host: new SucceedingHost());
        var run = Run();

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = run });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(2, rig.Provider.Requests.Count);
        var firstCatalog = SkillSystemPayload(rig.Provider.Requests[0]);
        var secondCatalog = SkillSystemPayload(rig.Provider.Requests[1]);
        Assert.Contains("CATALOG_DESCRIPTION_V1", firstCatalog);
        Assert.DoesNotContain("CATALOG_PROMPT_V1", firstCatalog);
        Assert.DoesNotContain("CATALOG_DESCRIPTION_V2", firstCatalog);
        Assert.Contains("CATALOG_DESCRIPTION_V2", secondCatalog);
        Assert.DoesNotContain("CATALOG_PROMPT_V2", secondCatalog);
        Assert.DoesNotContain("CATALOG_DESCRIPTION_V1", secondCatalog);

        var snapshots = (await rig.Store.ReadRunAsync(run.RunId, default))
            .Where(item => item.Kind == RuntimeEventKinds.TurnSnapshot)
            .ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.NotEqual(
            snapshots[0].Payload
                .GetProperty("extensions")
                .GetProperty("skillAdmission")
                .GetProperty("admissionDigest")
                .GetString(),
            snapshots[1].Payload
                .GetProperty("extensions")
                .GetProperty("skillAdmission")
                .GetProperty("admissionDigest")
                .GetString());
    }

    [Fact]
    public async Task ResumeWithoutActiveSkillDoesNotReplayPriorSkillInstructions()
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("advance_turn", "1.0.0") },
            new[]
            {
                Skill(
                    "resume-skill",
                    prompt: "ACTIVE_ONLY_RESUME_MARKER",
                    description: "RESUME_CATALOG_DESCRIPTION")
            },
            providerFactory: _ => new RecordingProvider(
                request => ToolCallEvents(
                    request,
                    "resume-skill-call",
                    "advance_turn"),
                FinalEvents),
            host: new UnknownReceiptHost());
        var run = Run();

        var initial = await rig.Runtime.RunAsync(
            Request(run, "resume-skill"));

        Assert.Equal(RunStates.Reconciling, initial.Run.State);
        Assert.Contains(
            "ACTIVE_ONLY_RESUME_MARKER",
            SkillSystemPayload(Assert.Single(rig.Provider.Requests)));

        var resumed = await rig.Runtime.ResumeAsync(
            run.RunId,
            new DurableRunContinuation
            {
                ReplaceActiveSkills = true
            },
            new SucceedingReconciler());

        Assert.Equal(RunStates.Completed, resumed.Run.State);
        Assert.Equal(2, rig.Provider.Requests.Count);
        var resumedSystem = SkillSystemPayload(rig.Provider.Requests[1]);
        Assert.Contains("RESUME_CATALOG_DESCRIPTION", resumedSystem);
        Assert.DoesNotContain("ACTIVE_ONLY_RESUME_MARKER", resumedSystem);
    }

    private static DurableRunRequest Request(AgentRun run, string skillId)
    {
        return new DurableRunRequest
        {
            Run = run,
            ActiveSkills = new[]
            {
                new SkillReference(skillId, "1.0.0")
            }
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
                MaxTurns = 4,
                MaxDurationMs = 30_000,
                MaxTokens = 4_000,
                MaxActions = 4,
                MaxCostUsd = "1"
            },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static SkillManifest Skill(
        string id,
        string trust = "trusted",
        string prompt = "Follow the admitted skill.",
        string? description = null,
        string capabilities = "{}",
        string activationPolicy = "{}",
        IReadOnlyList<string>? requiredTools = null)
    {
        return new SkillManifest
        {
            SkillId = id,
            Version = "1.0.0",
            Digest = $"declared:{id}",
            Description = description ?? $"{id} description",
            PromptFragments = new List<string> { prompt },
            RequiredToolRefs = requiredTools?.ToList() ?? new List<string>(),
            CapabilityRequirements = Json(capabilities),
            ActivationPolicy = Json(activationPolicy),
            Trust = trust
        };
    }

    private static ToolDescriptor Tool(string name, string version)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = version,
            Description = "Read a world value.",
            ParametersSchema = Json(
                """{"type":"object","additionalProperties":false}"""),
            Effect = ToolEffects.PureRead,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 2_000,
            RetryPolicy = "never",
            IdempotencyPolicy = "none",
            Toolset = "world",
            Visibility = "direct"
        };
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string SkillSystemPayload(StreamingModelRequest request)
    {
        return string.Join(
            "\n",
            request.Messages
                .Where(message => message.Role == NormalizedRoles.System)
                .SelectMany(message => message.Parts)
                .Where(part => part.Json.HasValue)
                .Select(part => part.Json!.Value.GetRawText()));
    }

    private static IEnumerable<ModelStreamEvent> ToolCallEvents(
        StreamingModelRequest request,
        string callId,
        string toolName)
    {
        return new[]
        {
            new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = callId,
                ToolNameDelta = toolName,
                ArgumentsJsonDelta = "{}"
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
                FinishReason = "tool_calls"
            }
        };
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

    private sealed class RuntimeRig : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly JournalCoordinator _journal;

        public RuntimeRig(
            IReadOnlyList<ToolDescriptor> tools,
            IReadOnlyList<SkillManifest> skills,
            Func<ToolCatalogRegistry, ISkillAdmissionPolicy>? policyFactory =
                null,
            Func<SkillCatalogRegistry, RecordingProvider>? providerFactory =
                null,
            IGameHost? host = null)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "game-agent-skill-admission-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Store = new FileSessionStore(
                Path.Combine(_directory, "runtime.journal"));
            Tools = new ToolCatalogRegistry();
            Tools.Replace(tools);
            Skills = new SkillCatalogRegistry();
            Skills.Replace(skills);
            Provider = providerFactory?.Invoke(Skills)
                       ?? new RecordingProvider();
            var clock = new SystemRuntimeClock();
            var ids = new GuidRuntimeIdGenerator();
            _journal = new JournalCoordinator(
                Store,
                Store,
                clock,
                ids);
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
                new RunRecovery(Store, Store, _journal),
                Tools,
                Skills,
                new ContextCompiler(),
                new ToolBatchPlanner(),
                new ToolBatchScheduler(),
                clock,
                ids,
                new DurableAgentRuntimeOptions
                {
                    ModelId = "skill-test-model",
                    MaxConcurrentProviderCalls = 1
                },
                skillAdmissionPolicy:
                    policyFactory?.Invoke(Tools));
        }

        public FileSessionStore Store { get; }

        public RecordingProvider Provider { get; }

        public ToolCatalogRegistry Tools { get; }

        public SkillCatalogRegistry Skills { get; }

        public DurableAgentRuntime Runtime { get; }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            _journal.Dispose();
            await Store.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ReplacingAdmissionPolicy : ISkillAdmissionPolicy
    {
        private readonly ToolCatalogRegistry _tools;
        private int _replaced;

        public ReplacingAdmissionPolicy(ToolCatalogRegistry tools)
        {
            _tools = tools;
        }

        public string PolicyId => "replace-during-admission";

        public string Version => "1.0.0";

        public SkillAdmissionDecision Evaluate(SkillAdmissionRequest request)
        {
            if (request.IsExplicitActivation
                && Interlocked.Exchange(ref _replaced, 1) == 0)
            {
                _tools.Replace(new[] { Tool("world.lookup", "2.0.0") });
            }

            return SkillAdmissionDecision.Allow();
        }
    }

    private sealed class ExplicitAllowPolicy : ISkillAdmissionPolicy
    {
        private int _activationCalls;

        public string PolicyId => "game-skill-policy";

        public string Version => "3.1.0";

        public int ActivationCalls => Volatile.Read(ref _activationCalls);

        public SkillAdmissionDecision Evaluate(SkillAdmissionRequest request)
        {
            if (request.IsExplicitActivation)
            {
                Interlocked.Increment(ref _activationCalls);
            }

            return SkillAdmissionDecision.Allow("game_skill_approved");
        }
    }

    private sealed class ThrowingAdmissionPolicy : ISkillAdmissionPolicy
    {
        public string PolicyId => "throwing-skill-policy";

        public string Version => "1.0.0";

        public SkillAdmissionDecision Evaluate(SkillAdmissionRequest request)
        {
            _ = request;
            throw new InvalidOperationException(
                "Application policy failed.");
        }
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

    private sealed class SucceedingHost : IGameHost
    {
        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 0,
                    Status = ReceiptStatuses.Succeeded,
                    Result = Json("""{"advanced":true}"""),
                    ReceivedAt = now,
                    CommittedAt = now
                });
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

    private sealed class RecordingProvider : IStreamingModelProvider
    {
        private readonly Queue<
            Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>> _steps;

        public RecordingProvider(
            params Func<
                StreamingModelRequest,
                IEnumerable<ModelStreamEvent>>[] steps)
        {
            _steps = new Queue<
                Func<StreamingModelRequest, IEnumerable<ModelStreamEvent>>>(
                steps);
        }

        public string ProviderId => "skill-test-provider";

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
            var events = _steps.Count == 0
                ? FinalEvents(request)
                : _steps.Dequeue()(request);
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }
}
