using System.Runtime.CompilerServices;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class ToolDisclosureTests
{
    [Fact]
    public async Task DeferredSearchUsesParameterNamesAndKeepsExactNameBoost()
    {
        var exact = Tool(
            "world.weather",
            ToolVisibilities.Deferred,
            description: "Read the current conditions.");
        var noisy = Tool(
            "world.weather.details",
            ToolVisibilities.Deferred,
            description: string.Join(
                " ",
                Enumerable.Repeat("world weather forecast", 40)));
        var parameterMatch = Tool(
            "world.sensor.read",
            ToolVisibilities.Deferred,
            description: "Read one sensor.");
        parameterMatch.ParametersSchema = Json(
            """
            {
              "type": "object",
              "properties": {
                "weatherStationId": { "type": "string" }
              },
              "required": [ "weatherStationId" ],
              "additionalProperties": false
            }
            """);
        await using var rig = new RuntimeRig(
            new[] { noisy, parameterMatch, exact },
            Array.Empty<SkillManifest>(),
            _ => new ScriptedProvider(
                request => ToolCalls(
                    request,
                    new Call(
                        "exact-search",
                        ToolDisclosureControlNames.Search,
                        """{"query":"world.weather","limit":3}"""),
                    new Call(
                        "parameter-search",
                        ToolDisclosureControlNames.Search,
                        """{"query":"station","limit":3}""")),
                Final),
            host: new SucceedingHost());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        var exactResults = ToolResult(outcome, "exact-search")
            .GetProperty("results");
        Assert.Equal(
            "world.weather",
            exactResults[0].GetProperty("name").GetString());
        var parameterResults = ToolResult(outcome, "parameter-search")
            .GetProperty("results");
        Assert.Equal(
            "world.sensor.read",
            Assert.Single(parameterResults.EnumerateArray())
                .GetProperty("name")
                .GetString());
    }

    [Fact]
    public async Task SearchActivationAndExecutionProgressAcrossProviderTurns()
    {
        var weather = Tool(
            "world.weather",
            ToolVisibilities.Deferred,
            description: "查询世界天气和温度。");
        await using var rig = new RuntimeRig(
            new[] { weather },
            Array.Empty<SkillManifest>(),
            tools =>
            {
                Assert.True(tools.Current.TryGet("world.weather", out var entry));
                return new ScriptedProvider(
                    request => ToolCalls(
                        request,
                        new Call(
                            "search-call",
                            ToolDisclosureControlNames.Search,
                            """{"query":"天气","limit":4}""")),
                    request => ToolCalls(
                        request,
                        new Call(
                            "activate-call",
                            ToolDisclosureControlNames.Activate,
                            JsonSerializer.Serialize(
                                new
                                {
                                    name = entry!.Name,
                                    version = entry.Version,
                                    descriptorDigest = entry.Digest
                                }))),
                    request => ToolCalls(
                        request,
                        new Call("weather-call", "world.weather", "{}")),
                    Final);
            },
            host: new SucceedingHost());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(4, rig.Provider.Requests.Count);
        Assert.Equal(
            new[]
            {
                ToolDisclosureControlNames.Activate,
                ToolDisclosureControlNames.Search
            },
            rig.Provider.Requests[0].Tools
                .Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal));
        Assert.DoesNotContain(
            rig.Provider.Requests[1].Tools,
            item => item.Name == "world.weather");
        Assert.Contains(
            rig.Provider.Requests[2].Tools,
            item => item.Name == "world.weather");
        Assert.DoesNotContain(
            rig.Provider.Requests[2].Tools,
            item => ToolDisclosureControlNames.IsReserved(item.Name));

        var search = ToolResult(outcome, "search-call");
        var result = Assert.Single(
            search.GetProperty("results").EnumerateArray());
        Assert.Equal("world.weather", result.GetProperty("name").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(
                result.GetProperty("descriptorDigest").GetString()));
        var activation = ToolResult(outcome, "activate-call");
        Assert.True(activation.GetProperty("activated").GetBoolean());
        Assert.Equal(
            "next_provider_turn",
            activation.GetProperty("availableFrom").GetString());
        Assert.Single(((SucceedingHost)rig.Host).Requests);

        var firstSearchSchema = rig.Provider.Requests[0].Tools
            .Single(
                item => item.Name == ToolDisclosureControlNames.Search)
            .ParametersSchema;
        Assert.False(
            firstSearchSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(
            firstSearchSchema.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "query");
        Assert.DoesNotContain(
            rig.Provider.Requests[0].Tools,
            item => item.Name.Contains("proxy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchControlsEnforceResultQueryAndPerTurnCaps()
    {
        await using var rig = new RuntimeRig(
            new[]
            {
                Tool(
                    "world.read.alpha",
                    ToolVisibilities.Deferred,
                    description: "Read alpha."),
                Tool(
                    "world.read.beta",
                    ToolVisibilities.Deferred,
                    description: "Read beta."),
                Tool(
                    "world.read.gamma",
                    ToolVisibilities.Deferred,
                    description: "Read gamma.")
            },
            Array.Empty<SkillManifest>(),
            _ => new ScriptedProvider(
                request => ToolCalls(
                    request,
                    new Call(
                        "bounded-results",
                        ToolDisclosureControlNames.Search,
                        """{"query":"read","limit":2}"""),
                    new Call(
                        "utf8-query-over-limit",
                        ToolDisclosureControlNames.Search,
                        """{"query":"天气"}"""),
                    new Call(
                        "control-call-over-limit",
                        ToolDisclosureControlNames.Search,
                        """{"query":"read"}""")),
                Final),
            host: new SucceedingHost(),
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "disclosure-test-model",
                MaxConcurrentProviderCalls = 1,
                ToolDisclosureLimits = new ToolDisclosureLimits(
                    maxSearchResults: 2,
                    maxControlCallsPerTurn: 2,
                    maxSearchQueryUtf8Bytes: 4)
            });

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        var bounded = ToolResult(outcome, "bounded-results");
        Assert.Equal(2, bounded.GetProperty("count").GetInt32());
        Assert.Equal(
            2,
            bounded.GetProperty("results").GetArrayLength());
        Assert.Equal(
            "tool_disclosure_arguments_invalid",
            ToolResult(outcome, "utf8-query-over-limit")
                .GetProperty("code")
                .GetString());
        Assert.Equal(
            "tool_disclosure_control_limit_exceeded",
            ToolResult(outcome, "control-call-over-limit")
                .GetProperty("code")
                .GetString());
        Assert.Empty(((SucceedingHost)rig.Host).Requests);
        Assert.DoesNotContain(
            await rig.Store.ReadRunAsync(outcome.Run.RunId, default),
            item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(
        """{"name":"world.hidden","version":"1.0.0","descriptorDigest":"digest","extra":"value"}""")]
    [InlineData(
        """{"name":"world.hidden","version":1,"descriptorDigest":"digest"}""")]
    public async Task InvalidActivationArgumentsFailWithoutStateChange(
        string arguments)
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("world.hidden", ToolVisibilities.Deferred) },
            Array.Empty<SkillManifest>(),
            _ => new ScriptedProvider(
                request => ToolCalls(
                    request,
                    new Call(
                        "invalid-activation",
                        ToolDisclosureControlNames.Activate,
                        arguments)),
                Final),
            host: new SucceedingHost());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(
            "tool_disclosure_arguments_invalid",
            ToolResult(outcome, "invalid-activation")
                .GetProperty("code")
                .GetString());
        Assert.DoesNotContain(
            rig.Provider.Requests[1].Tools,
            item => item.Name == "world.hidden");
        Assert.Empty(((SucceedingHost)rig.Host).Requests);
        Assert.DoesNotContain(
            await rig.Store.ReadRunAsync(outcome.Run.RunId, default),
            item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged);
    }

    [Theory]
    [InlineData(true, false, ToolDisclosureReasonCodes.NotDeferred)]
    [InlineData(
        false,
        true,
        ToolDisclosureReasonCodes.ExactIdentityMismatch)]
    public async Task ActivationRequiresExactNameAndDescriptorDigest(
        bool wrongName,
        bool wrongDigest,
        string expectedReasonCode)
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("world.exact", ToolVisibilities.Deferred) },
            Array.Empty<SkillManifest>(),
            tools =>
            {
                Assert.True(tools.Current.TryGet("world.exact", out var entry));
                return new ScriptedProvider(
                    request => ToolCalls(
                        request,
                        new Call(
                            "inexact-activation",
                            ToolDisclosureControlNames.Activate,
                            JsonSerializer.Serialize(
                                new
                                {
                                    name = wrongName
                                        ? "world.missing"
                                        : entry!.Name,
                                    version = entry!.Version,
                                    descriptorDigest = wrongDigest
                                        ? "sha256:not-the-descriptor"
                                        : entry.Digest
                                }))),
                    Final);
            },
            host: new SucceedingHost());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        var activation = ToolResult(outcome, "inexact-activation");
        Assert.False(activation.GetProperty("activated").GetBoolean());
        Assert.Equal(
            expectedReasonCode,
            activation.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(
            rig.Provider.Requests[1].Tools,
            item => item.Name == "world.exact");
        Assert.Empty(((SucceedingHost)rig.Host).Requests);
        Assert.DoesNotContain(
            await rig.Store.ReadRunAsync(outcome.Run.RunId, default),
            item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged);
    }

    [Fact]
    public async Task ActivationDoesNotMakeSiblingCallCallableInSameResponse()
    {
        var deferred = Tool("world.scan", ToolVisibilities.Deferred);
        await using var rig = new RuntimeRig(
            new[] { deferred },
            Array.Empty<SkillManifest>(),
            tools =>
            {
                Assert.True(tools.Current.TryGet("world.scan", out var entry));
                return new ScriptedProvider(
                    request => ToolCalls(
                        request,
                        new Call(
                            "activate-scan",
                            ToolDisclosureControlNames.Activate,
                            JsonSerializer.Serialize(
                                new
                                {
                                    name = entry!.Name,
                                    version = entry.Version,
                                    descriptorDigest = entry.Digest
                                })),
                        new Call("early-scan", "world.scan", "{}")),
                    Final);
            },
            host: new SucceedingHost());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(
            "unknown_tool",
            ToolResult(outcome, "early-scan").GetProperty("code").GetString());
        Assert.Empty(((SucceedingHost)rig.Host).Requests);
        Assert.Contains(
            rig.Provider.Requests[1].Tools,
            item => item.Name == "world.scan");
    }

    [Fact]
    public async Task SearchAndDirectCallsNeverRevealDeniedOrInternalTools()
    {
        var policy = new DenyNamedPolicy("world.denied");
        await using var rig = new RuntimeRig(
            new[]
            {
                Tool(
                    "world.allowed",
                    ToolVisibilities.Deferred,
                    description: "Weather lookup."),
                Tool(
                    "world.denied",
                    ToolVisibilities.Deferred,
                    description: "Secret denied lookup."),
                Tool(
                    "runtime.internal",
                    ToolVisibilities.Internal,
                    description: "Secret internal lookup.")
            },
            Array.Empty<SkillManifest>(),
            _ => new ScriptedProvider(
                request => ToolCalls(
                    request,
                    new Call(
                        "secret-search",
                        ToolDisclosureControlNames.Search,
                        """{"query":"secret"}"""),
                    new Call("denied-call", "world.denied", "{}"),
                    new Call("internal-call", "runtime.internal", "{}")),
                Final),
            disclosurePolicy: policy,
            host: new SucceedingHost());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Empty(
            ToolResult(outcome, "secret-search")
                .GetProperty("results")
                .EnumerateArray());
        Assert.Equal(
            "unknown_tool",
            ToolResult(outcome, "denied-call").GetProperty("code").GetString());
        Assert.Equal(
            "unknown_tool",
            ToolResult(outcome, "internal-call").GetProperty("code").GetString());
        Assert.All(
            rig.Provider.Requests.SelectMany(item => item.Tools),
            tool => Assert.DoesNotContain(
                tool.Name,
                new[] { "world.denied", "runtime.internal" }));
        Assert.DoesNotContain("runtime.internal", policy.SeenTools);
        Assert.Empty(((SucceedingHost)rig.Host).Requests);
    }

    [Fact]
    public async Task ActivationStateIsIsolatedPerRun()
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("world.private", ToolVisibilities.Deferred) },
            Array.Empty<SkillManifest>(),
            tools =>
            {
                Assert.True(
                    tools.Current.TryGet("world.private", out var deferred));
                return new ScriptedProvider(
                    request => ToolCalls(
                        request,
                        new Call(
                            "activate-private",
                            ToolDisclosureControlNames.Activate,
                            JsonSerializer.Serialize(
                                new
                                {
                                    name = deferred!.Name,
                                    version = deferred.Version,
                                    descriptorDigest = deferred.Digest
                                }))),
                    Final,
                    Final);
            });

        var first = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run("agent-a") });
        var second = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run("agent-b") });

        Assert.Equal(RunStates.Completed, first.Run.State);
        Assert.Equal(RunStates.Completed, second.Run.State);
        Assert.Contains(
            rig.Provider.Requests[1].Tools,
            item => item.Name == "world.private");
        Assert.DoesNotContain(
            rig.Provider.Requests[2].Tools,
            item => item.Name == "world.private");
        Assert.Contains(
            rig.Provider.Requests[2].Tools,
            item => item.Name == ToolDisclosureControlNames.Search);
    }

    [Fact]
    public async Task ThrowingDisclosurePolicyFailsClosed()
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("world.hidden", ToolVisibilities.Deferred) },
            Array.Empty<SkillManifest>(),
            _ => new ScriptedProvider(Final),
            disclosurePolicy: new ThrowingDisclosurePolicy());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Empty(Assert.Single(rig.Provider.Requests).Tools);
        var snapshot = Assert.Single(
            await rig.Store.ReadRunAsync(outcome.Run.RunId, default),
            item => item.Kind == RuntimeEventKinds.TurnSnapshot);
        var disclosure = snapshot.Payload
            .GetProperty("extensions")
            .GetProperty("toolDisclosure");
        Assert.Contains(
            disclosure.GetProperty("reasonCodes").EnumerateArray(),
            item => item.GetString()
                    == ToolDisclosureReasonCodes.PolicyError);
        Assert.False(
            snapshot.Payload.TryGetProperty(
                "deferredCatalogDigest",
                out _));
    }

    [Fact]
    public async Task CatalogReplacementWaitsForNextLoopInvocation()
    {
        await using var rig = new RuntimeRig(
            new[] { Tool("world.map", ToolVisibilities.Deferred) },
            Array.Empty<SkillManifest>(),
            tools =>
            {
                Assert.True(tools.Current.TryGet("world.map", out var original));
                return new ScriptedProvider(
                    request =>
                    {
                        tools.Replace(
                            new[]
                            {
                                Tool(
                                    "world.map",
                                    ToolVisibilities.Deferred,
                                    description: "Changed descriptor.")
                            });
                        return ToolCalls(
                            request,
                            new Call(
                                "activate-map",
                                ToolDisclosureControlNames.Activate,
                                JsonSerializer.Serialize(
                                    new
                                    {
                                        name = original!.Name,
                                        version = original.Version,
                                        descriptorDigest = original.Digest
                                    })));
                    },
                    Final,
                    Final);
            });

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });
        var nextOutcome = await rig.Runtime.RunAsync(
            new DurableRunRequest { Run = Run() });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Equal(RunStates.Completed, nextOutcome.Run.State);
        Assert.Contains(
            rig.Provider.Requests[1].Tools,
            item => item.Name == "world.map");
        Assert.DoesNotContain(
            rig.Provider.Requests[2].Tools,
            item => item.Name == "world.map");
        Assert.Contains(
            rig.Provider.Requests[2].Tools,
            item => item.Name == ToolDisclosureControlNames.Activate);
        var snapshots = (await rig.Store.ReadRunAsync(
                outcome.Run.RunId,
                default))
            .Where(item => item.Kind == RuntimeEventKinds.TurnSnapshot)
            .ToArray();
        var secondDisclosure = snapshots[1].Payload
            .GetProperty("extensions")
            .GetProperty("toolDisclosure");
        Assert.Single(
            secondDisclosure
                .GetProperty("activatedDeferred")
                .EnumerateArray());
        Assert.Single(
            secondDisclosure
                .GetProperty("requestedActivations")
                .EnumerateArray());
        Assert.DoesNotContain(
            secondDisclosure.GetProperty("reasonCodes").EnumerateArray(),
            item => item.GetString()
                    == ToolDisclosureReasonCodes.CatalogEntryChanged);
    }

    [Fact]
    public async Task RequiredDeferredToolIsCallableSameTurnAndOptionalStaysHidden()
    {
        var required = Tool("world.required", ToolVisibilities.Deferred);
        var optional = Tool("world.optional", ToolVisibilities.Deferred);
        var skill = Skill(
            "deferred-skill",
            requiredTools: new[] { "world.required@1.0.0" },
            optionalTools: new[] { "world.optional@1.0.0" });
        await using var rig = new RuntimeRig(
            new[] { required, optional },
            new[] { skill },
            _ => new ScriptedProvider(
                request => ToolCalls(
                    request,
                    new Call("required-call", "world.required", "{}")),
                Final),
            host: new SucceedingHost());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = Run(),
                ActiveSkills = new[]
                {
                    new SkillReference("deferred-skill", "1.0.0")
                }
            });

        Assert.Equal(RunStates.Completed, outcome.Run.State);
        Assert.Contains(
            rig.Provider.Requests[0].Tools,
            item => item.Name == "world.required");
        Assert.DoesNotContain(
            rig.Provider.Requests[0].Tools,
            item => item.Name == "world.optional");
        Assert.Single(((SucceedingHost)rig.Host).Requests);
        var events = await rig.Store.ReadRunAsync(outcome.Run.RunId, default);
        var disclosureEvent = Assert.Single(
            events,
            item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged);
        var snapshot = Assert.Single(
            events,
            item => item.Kind == RuntimeEventKinds.TurnSnapshot
                    && item.TurnId == disclosureEvent.TurnId);
        Assert.True(disclosureEvent.Sequence < snapshot.Sequence);
    }

    [Theory]
    [InlineData(
        ToolVisibilities.Internal,
        SkillAdmissionReasonCodes.RequiredToolNotDisclosable)]
    [InlineData(
        ToolVisibilities.Deferred,
        SkillAdmissionReasonCodes.RequiredToolDisclosureDenied)]
    public async Task RequiredToolDisclosureFailuresAreStable(
        string visibility,
        string expectedCode)
    {
        var policy = new DenyNamedPolicy("world.required");
        await using var rig = new RuntimeRig(
            new[] { Tool("world.required", visibility) },
            new[]
            {
                Skill(
                    "blocked-skill",
                    requiredTools: new[] { "world.required@1.0.0" })
            },
            _ => new ScriptedProvider(),
            disclosurePolicy: policy);

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = Run(),
                ActiveSkills = new[]
                {
                    new SkillReference("blocked-skill", "1.0.0")
                }
            });

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(expectedCode, outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
    }

    [Fact]
    public async Task RequiredDeferredToolsFailAtomicallyWhenCapacityIsExceeded()
    {
        await using var rig = new RuntimeRig(
            new[]
            {
                Tool("world.one", ToolVisibilities.Deferred),
                Tool("world.two", ToolVisibilities.Deferred)
            },
            new[]
            {
                Skill(
                    "wide-skill",
                    requiredTools:
                    new[] { "world.one@1.0.0", "world.two@1.0.0" })
            },
            _ => new ScriptedProvider(),
            options: new DurableAgentRuntimeOptions
            {
                ModelId = "disclosure-test-model",
                MaxConcurrentProviderCalls = 1,
                ToolDisclosureLimits = new ToolDisclosureLimits(
                    maxActivatedDeferredTools: 1)
            });

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = Run(),
                ActiveSkills = new[]
                {
                    new SkillReference("wide-skill", "1.0.0")
                }
            });

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillAdmissionReasonCodes
                .RequiredToolDisclosureCapacityExceeded,
            outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
        Assert.DoesNotContain(
            await rig.Store.ReadRunAsync(outcome.Run.RunId, default),
            item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged);
    }

    [Fact]
    public async Task PurposeSpecificDisclosureDenialsFailClosedBeforeProvider()
    {
        var policy = new PurposeSpecificPolicy(
            ToolDisclosurePurposes.Search,
            ToolDisclosurePurposes.SkillActivation);
        await using var rig = new RuntimeRig(
            new[] { Tool("world.purpose", ToolVisibilities.Deferred) },
            new[]
            {
                Skill(
                    "purpose-skill",
                    requiredTools: new[] { "world.purpose@1.0.0" })
            },
            _ => new ScriptedProvider(),
            disclosurePolicy: policy,
            host: new SucceedingHost());

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = Run(),
                ActiveSkills = new[]
                {
                    new SkillReference("purpose-skill", "1.0.0")
                }
            });

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillAdmissionReasonCodes.RequiredToolDisclosureDenied,
            outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
        Assert.Empty(((SucceedingHost)rig.Host).Requests);
        Assert.Equal(
            new[]
            {
                ToolDisclosurePurposes.ModelActivation,
                ToolDisclosurePurposes.Revalidation,
                ToolDisclosurePurposes.Search,
                ToolDisclosurePurposes.SkillActivation
            },
            policy.SeenPurposes.OrderBy(value => value, StringComparer.Ordinal));
        Assert.DoesNotContain(
            await rig.Store.ReadRunAsync(outcome.Run.RunId, default),
            item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged);
    }

    [Fact]
    public async Task NullDisclosureDecisionFailsClosedBeforeProvider()
    {
        var policy = new NullDisclosurePolicy();
        await using var rig = new RuntimeRig(
            new[] { Tool("world.null-policy", ToolVisibilities.Deferred) },
            new[]
            {
                Skill(
                    "null-policy-skill",
                    requiredTools: new[] { "world.null-policy@1.0.0" })
            },
            _ => new ScriptedProvider(),
            disclosurePolicy: policy,
            host: new SucceedingHost());

        var plan = new ToolDisclosureEvaluator(policy).Evaluate(
            Run(),
            "turn-null-policy",
            rig.Tools.Current,
            Array.Empty<ToolActivationRecord>(),
            new ToolDisclosureLimits());
        Assert.Empty(plan.EffectiveProviderTools);
        Assert.Contains(
            plan.ToSnapshotExtension()
                .GetProperty("reasonCodes")
                .EnumerateArray(),
            item => item.GetString()
                    == ToolDisclosureReasonCodes.PolicyDecisionInvalid);

        var outcome = await rig.Runtime.RunAsync(
            new DurableRunRequest
            {
                Run = Run(),
                ActiveSkills = new[]
                {
                    new SkillReference("null-policy-skill", "1.0.0")
                }
            });

        Assert.Equal(RunStates.Failed, outcome.Run.State);
        Assert.Equal(
            SkillAdmissionReasonCodes.RequiredToolDisclosureDenied,
            outcome.ErrorCode);
        Assert.Empty(rig.Provider.Requests);
        Assert.Empty(((SucceedingHost)rig.Host).Requests);
        Assert.DoesNotContain(
            await rig.Store.ReadRunAsync(outcome.Run.RunId, default),
            item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged);
    }

    [Fact]
    public void DescriptorMismatchRevokesStateAndFailsActiveSkillClosed()
    {
        var tools = new ToolCatalogRegistry();
        tools.Replace(
            new[] { Tool("world.required", ToolVisibilities.Deferred) });
        var skills = new SkillCatalogRegistry();
        skills.Replace(
            new[]
            {
                Skill(
                    "digest-skill",
                    requiredTools: new[] { "world.required@1.0.0" })
            });
        var run = Run();
        var evaluator = new ToolDisclosureEvaluator(policy: null);
        var first = evaluator.Evaluate(
            run,
            "turn-1",
            tools.Current,
            Array.Empty<ToolActivationRecord>(),
            new ToolDisclosureLimits());
        Assert.True(
            tools.Current.TryGet("world.required", out var original));
        Assert.Equal(
            ToolDisclosureReasonCodes.Allowed,
            first.ValidateRequiredTools(
                new[] { original! },
                "digest-skill@1.0.0",
                activate: true));
        var activated = first.RequestedActivations;

        tools.Replace(
            new[]
            {
                Tool(
                    "world.required",
                    ToolVisibilities.Deferred,
                    description: "Descriptor changed without a version bump.")
            });
        var second = evaluator.Evaluate(
            run,
            "turn-2",
            tools.Current,
            activated,
            new ToolDisclosureLimits());

        Assert.True(second.StateChanged);
        Assert.Empty(second.RequestedActivations);
        var exception = Assert.Throws<SkillAdmissionException>(
            () => new SkillAdmissionEvaluator(policy: null).Evaluate(
                run,
                "turn-2",
                skills.Current,
                tools.Current,
                second,
                new[]
                {
                    new SkillReference("digest-skill", "1.0.0")
                },
                new SkillDisclosureBudget()));
        Assert.Equal(
            SkillAdmissionReasonCodes.RequiredToolDescriptorMismatch,
            exception.ReasonCode);
    }

    [Fact]
    public void ExplicitModelActivationPromotesSkillOrigin()
    {
        var tools = new ToolCatalogRegistry();
        tools.Replace(
            new[] { Tool("world.shared", ToolVisibilities.Deferred) });
        Assert.True(tools.Current.TryGet("world.shared", out var entry));
        var run = Run();
        var evaluator = new ToolDisclosureEvaluator(policy: null);
        var first = evaluator.Evaluate(
            run,
            "turn-1",
            tools.Current,
            Array.Empty<ToolActivationRecord>(),
            new ToolDisclosureLimits());
        Assert.Equal(
            ToolDisclosureReasonCodes.Allowed,
            first.ValidateRequiredTools(
                new[] { entry! },
                "skill@1.0.0",
                activate: true));
        Assert.StartsWith(
            "skill:",
            Assert.Single(first.RequestedActivations).Origin);

        Assert.Equal(
            ToolDisclosureReasonCodes.ActivatedByModel,
            first.ActivateFromModel(
                entry!.Name,
                entry.Version,
                entry.Digest));
        Assert.Equal(
            "model",
            Assert.Single(first.RequestedActivations).Origin);

        var second = evaluator.Evaluate(
            run,
            "turn-2",
            tools.Current,
            first.RequestedActivations,
            new ToolDisclosureLimits());
        second.FinalizeSkillActivations();
        Assert.Single(second.EffectiveActivatedDeferred);
        Assert.Single(second.RequestedActivations);
    }

    [Fact]
    public async Task MixedGameAndActivationBatchRecoversExactState()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-tool-disclosure-recovery-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new FileSessionStore(
                Path.Combine(directory, "runtime.journal"));
            var descriptors = new[]
            {
                Tool("advance", ToolVisibilities.Direct),
                Tool("world.recovered", ToolVisibilities.Deferred)
            };
            var tools = new ToolCatalogRegistry();
            tools.Replace(descriptors);
            Assert.True(
                tools.Current.TryGet("world.recovered", out var deferred));
            var firstProvider = new ScriptedProvider(
                request => ToolCalls(
                    request,
                    new Call("advance-call", "advance", "{}"),
                    new Call(
                        "recover-activation",
                        ToolDisclosureControlNames.Activate,
                        JsonSerializer.Serialize(
                            new
                            {
                                name = deferred!.Name,
                                version = deferred.Version,
                                descriptorDigest = deferred.Digest
                            }))));
            var clock = new SystemRuntimeClock();
            var ids = new GuidRuntimeIdGenerator();
            var firstJournal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            var run = Run();
            await using (var firstRuntime = CreateRuntime(
                             store,
                             store,
                             tools,
                             new SkillCatalogRegistry(),
                             firstProvider,
                             new UnknownHost(),
                             firstJournal,
                             clock,
                             ids))
            {
                var interrupted = await firstRuntime.RunAsync(
                    new DurableRunRequest { Run = run });
                Assert.Equal(RunStates.Reconciling, interrupted.Run.State);
            }

            firstJournal.Dispose();
            var events = await store.ReadRunAsync(run.RunId, default);
            var stateEvent = Assert.Single(
                events,
                item => item.Kind == RuntimeEventKinds.ToolDisclosureChanged);
            Assert.Contains(
                events,
                item => item.Kind == RuntimeEventKinds.TranscriptMessage
                        && item.Sequence == stateEvent.Sequence + 1);

            var secondProvider = new ScriptedProvider(Final);
            var secondJournal = new JournalCoordinator(
                store,
                store,
                clock,
                ids);
            await using (var secondRuntime = CreateRuntime(
                             store,
                             store,
                             tools,
                             new SkillCatalogRegistry(),
                             secondProvider,
                             new SucceedingHost(),
                             secondJournal,
                             clock,
                             ids))
            {
                var resumed = await secondRuntime.ResumeAsync(
                    run.RunId,
                    reconciler: new SucceedingReconciler());
                Assert.Equal(RunStates.Completed, resumed.Run.State);
                Assert.Contains(
                    Assert.Single(secondProvider.Requests).Tools,
                    item => item.Name == "world.recovered");
            }

            secondJournal.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DurableAgentRuntime CreateRuntime(
        IDurableSessionStore store,
        IOperationLedger operations,
        ToolCatalogRegistry tools,
        SkillCatalogRegistry skills,
        IStreamingModelProvider provider,
        IGameHost host,
        JournalCoordinator journal,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        DurableAgentRuntimeOptions? options = null,
        IToolDisclosurePolicy? disclosurePolicy = null)
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
                ids),
            host,
            journal,
            new RunRecovery(store, operations, journal),
            tools,
            skills,
            new ContextCompiler(),
            new ToolBatchPlanner(),
            new ToolBatchScheduler(),
            clock,
            ids,
            options
            ?? new DurableAgentRuntimeOptions
            {
                ModelId = "disclosure-test-model",
                MaxConcurrentProviderCalls = 1
            },
            toolDisclosurePolicy: disclosurePolicy);
    }

    private static AgentRun Run(string agentId = "agent-1")
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            AgentId = agentId,
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Queued,
            Budget = new AgentBudget
            {
                MaxTurns = 8,
                MaxDurationMs = 30_000,
                MaxTokens = 20_000,
                MaxActions = 8,
                MaxCostUsd = "1"
            },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ToolDescriptor Tool(
        string name,
        string visibility,
        string description = "Read a world value.",
        string version = "1.0.0")
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = version,
            Description = description,
            ParametersSchema = Json(
                """{"type":"object","additionalProperties":false}"""),
            Effect = ToolEffects.PureRead,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 2_000,
            RetryPolicy = ToolRetryPolicies.Never,
            IdempotencyPolicy = ToolIdempotencyPolicies.None,
            Toolset = "world",
            Visibility = visibility
        };
    }

    private static SkillManifest Skill(
        string id,
        IReadOnlyList<string>? requiredTools = null,
        IReadOnlyList<string>? optionalTools = null)
    {
        return new SkillManifest
        {
            SkillId = id,
            Version = "1.0.0",
            Digest = "declared:" + id,
            Description = id + " description",
            PromptFragments = new List<string> { "Use the admitted skill." },
            RequiredToolRefs =
                requiredTools?.ToList() ?? new List<string>(),
            OptionalToolRefs =
                optionalTools?.ToList() ?? new List<string>(),
            CapabilityRequirements = Json("{}"),
            ActivationPolicy = Json("{}"),
            Trust = "trusted"
        };
    }

    private static JsonElement ToolResult(
        DurableRunOutcome outcome,
        string toolCallId)
    {
        return outcome.Transcript
            .SelectMany(item => item.Parts)
            .Single(
                part => part.Type == NormalizedPartTypes.ToolResult
                        && part.ToolCallId == toolCallId)
            .Json!.Value;
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static IEnumerable<ModelStreamEvent> ToolCalls(
        StreamingModelRequest request,
        params Call[] calls)
    {
        var ordinal = 0L;
        foreach (var call in calls)
        {
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = ordinal++,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = call.Id,
                ToolNameDelta = call.Name,
                ArgumentsJsonDelta = call.Arguments
            };
        }

        yield return new ModelStreamEvent
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
        };
        yield return new ModelStreamEvent
        {
            StreamAttemptId = request.StreamAttemptId,
            Ordinal = ordinal,
            Kind = ModelStreamEventKinds.Completed,
            FinishReason = "tool_calls"
        };
    }

    private static IEnumerable<ModelStreamEvent> Final(
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

    private sealed record Call(string Id, string Name, string Arguments);

    private sealed class RuntimeRig : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly JournalCoordinator _journal;

        public RuntimeRig(
            IReadOnlyList<ToolDescriptor> tools,
            IReadOnlyList<SkillManifest> skills,
            Func<ToolCatalogRegistry, ScriptedProvider> providerFactory,
            IToolDisclosurePolicy? disclosurePolicy = null,
            IGameHost? host = null,
            DurableAgentRuntimeOptions? options = null)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "game-agent-tool-disclosure-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Store = new FileSessionStore(
                Path.Combine(_directory, "runtime.journal"));
            Tools = new ToolCatalogRegistry();
            Tools.Replace(tools);
            var skillRegistry = new SkillCatalogRegistry();
            skillRegistry.Replace(skills);
            Provider = providerFactory(Tools);
            Host = host ?? new SucceedingHost();
            var clock = new SystemRuntimeClock();
            var ids = new GuidRuntimeIdGenerator();
            _journal = new JournalCoordinator(Store, Store, clock, ids);
            Runtime = CreateRuntime(
                Store,
                Store,
                Tools,
                skillRegistry,
                Provider,
                Host,
                _journal,
                clock,
                ids,
                options,
                disclosurePolicy);
        }

        public FileSessionStore Store { get; }

        public ToolCatalogRegistry Tools { get; }

        public ScriptedProvider Provider { get; }

        public IGameHost Host { get; }

        public DurableAgentRuntime Runtime { get; }

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

        public string ProviderId => "tool-disclosure-provider";

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
                ? Final(request)
                : _steps.Dequeue()(request);
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class SucceedingHost : IGameHost
    {
        public List<ActionRequest> Requests { get; } = new();

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var now = DateTimeOffset.UtcNow;
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 0,
                    Status = ReceiptStatuses.Succeeded,
                    Result = Json("{}"),
                    ReceivedAt = now,
                    CommittedAt = now
                });
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
                    Result = Json("{}"),
                    ReceivedAt = now,
                    CommittedAt = now
                });
        }
    }

    private sealed class DenyNamedPolicy : IToolDisclosurePolicy
    {
        private readonly HashSet<string> _denied;

        public DenyNamedPolicy(params string[] denied)
        {
            _denied = new HashSet<string>(denied, StringComparer.Ordinal);
        }

        public string PolicyId => "deny-named-tools";

        public string Version => "1.0.0";

        public HashSet<string> SeenTools { get; } =
            new(StringComparer.Ordinal);

        public ToolDisclosureDecision Evaluate(
            ToolDisclosureRequest request)
        {
            SeenTools.Add(request.Tool.Name);
            return _denied.Contains(request.Tool.Name)
                ? ToolDisclosureDecision.Deny("tool_denied_for_test")
                : ToolDisclosureDecision.Allow();
        }
    }

    private sealed class ThrowingDisclosurePolicy : IToolDisclosurePolicy
    {
        public string PolicyId => "throwing-disclosure-policy";

        public string Version => "1.0.0";

        public ToolDisclosureDecision Evaluate(
            ToolDisclosureRequest request)
        {
            _ = request;
            throw new InvalidOperationException("Policy failure.");
        }
    }

    private sealed class PurposeSpecificPolicy : IToolDisclosurePolicy
    {
        private readonly HashSet<string> _allowedPurposes;

        public PurposeSpecificPolicy(params string[] allowedPurposes)
        {
            _allowedPurposes = new HashSet<string>(
                allowedPurposes,
                StringComparer.Ordinal);
        }

        public string PolicyId => "purpose-specific-policy";

        public string Version => "1.0.0";

        public HashSet<string> SeenPurposes { get; } =
            new(StringComparer.Ordinal);

        public ToolDisclosureDecision Evaluate(
            ToolDisclosureRequest request)
        {
            SeenPurposes.Add(request.Purpose);
            return _allowedPurposes.Contains(request.Purpose)
                ? ToolDisclosureDecision.Allow()
                : ToolDisclosureDecision.Deny(
                    "purpose_denied_for_test");
        }
    }

    private sealed class NullDisclosurePolicy : IToolDisclosurePolicy
    {
        public string PolicyId => "null-disclosure-policy";

        public string Version => "1.0.0";

        public ToolDisclosureDecision Evaluate(
            ToolDisclosureRequest request)
        {
            _ = request;
            return null!;
        }
    }
}
