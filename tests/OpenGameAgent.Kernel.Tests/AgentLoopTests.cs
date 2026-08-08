using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class AgentLoopTests
{
    [Fact]
    public void CanonicalTranscriptRejectsUnresolvedOrMismatchedToolExchanges()
    {
        var call = new ToolCallContent("call", "inspect", "{}");
        var assistant = new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[] { call },
            DateTimeOffset.UnixEpoch,
            model: "test",
            stopReason: ModelStopReason.ToolUse);
        var unresolved = new AgentOptions(ScriptedProvider.FromResponses(Responses.Text("unused")), "test");
        unresolved.InitialMessages.Add(assistant);
        Assert.Throws<ArgumentException>(() => new Agent(unresolved));

        var mismatched = new AgentMessage(
            AgentRole.Tool,
            new AgentContent[] { new TextContent("result") },
            DateTimeOffset.UnixEpoch,
            toolCallId: call.Id,
            toolName: "different");
        Assert.Throws<ArgumentException>(() => AgentValidation.ValidateTranscript(
            new[] { assistant, mismatched },
            new AgentLimits()));

        Assert.Throws<ArgumentException>(() => new AgentMessage(
            AgentRole.User,
            new AgentContent[] { new ToolCallContent("forged", "inspect", "{}") },
            DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => new ToolResult(
            new AgentContent[] { new ReasoningContent("assistant-only") }));
        Assert.Throws<ArgumentException>(() => new ToolResult(
            new AgentContent[] { new ToolCallContent("nested", "inspect", "{}") }));
    }

    [Fact]
    public async Task StructuredInputIsProjectedWithoutMutatingCanonicalHistory()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("ok"));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                TransformContextAsync = (messages, _) =>
                    new ValueTask<IReadOnlyList<AgentMessage>>(
                        new[] { AgentMessage.User("projected", DateTimeOffset.UnixEpoch) }),
            },
            Clock = () => DateTimeOffset.UnixEpoch,
        };
        var agent = new Agent(options);

        var result = await agent.RunAsync(
            AgentMessage.UserJson("{\"tick\":12.5,\"alive\":true}", DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(provider.Requests);
        Assert.Equal("projected", Assert.IsType<TextContent>(Assert.Single(request.Messages[0].Content)).Text);
        var canonical = Assert.IsType<JsonContent>(Assert.Single(agent.State.Messages[0].Content));
        Assert.Contains("12.5", canonical.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulRunEmitsCompleteOrderedLifecycle()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("hello"));
        var agent = new Agent(new AgentOptions(provider, "test"));
        var events = new List<AgentEventKind>();
        agent.Subscribe((value, _) =>
        {
            events.Add(value.Kind);
            return ValueTask.CompletedTask;
        });

        var result = await agent.RunAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(
            new[]
            {
                AgentEventKind.RunStarted,
                AgentEventKind.TurnStarted,
                AgentEventKind.MessageStarted,
                AgentEventKind.MessageEnded,
                AgentEventKind.MessageStarted,
                AgentEventKind.MessageEnded,
                AgentEventKind.TurnEnded,
                AgentEventKind.RunEnded,
            },
            events);
    }

    [Fact]
    public async Task InitialSteeringPollOccursInsideTheFirstTurnAfterPromptEvents()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("hello"));
        var order = new List<string>();
        var options = new AgentLoopOptions(provider, "test")
        {
            GetSteeringMessagesAsync = _ =>
            {
                order.Add("poll");
                return new ValueTask<IReadOnlyList<AgentMessage>>(Array.Empty<AgentMessage>());
            },
        };

        var result = await AgentLoop.RunAsync(
            new[] { AgentMessage.User("hi", DateTimeOffset.UnixEpoch) },
            new AgentContext(string.Empty),
            options,
            (value, _) =>
            {
                order.Add(value.Kind.ToString());
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(order.IndexOf(AgentEventKind.TurnStarted.ToString()) < order.IndexOf("poll"));
        Assert.True(order.IndexOf(AgentEventKind.MessageEnded.ToString()) < order.IndexOf("poll"));
        Assert.True(order.IndexOf("poll") < order.LastIndexOf(AgentEventKind.MessageStarted.ToString()));
    }

    [Fact]
    public async Task ToolLoopPersistsAssistantCallResultAndFinalReply()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "look", "{}")),
            Responses.Text("done"));
        var executed = 0;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("look", (_, _, _) =>
        {
            Interlocked.Increment(ref executed);
            return new ValueTask<ToolResult>(new ToolResult(
                new AgentContent[] { new TextContent("seen") },
                usage: new ModelUsage(3, 2)));
        }));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, executed);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(
            new[] { AgentRole.User, AgentRole.Assistant, AgentRole.Tool, AgentRole.Assistant },
            agent.State.Messages.Select(message => message.Role));
        Assert.Equal("1", agent.State.Messages[2].ToolCallId);
        Assert.Equal(3, agent.State.Messages[2].Usage!.InputTokens);
        Assert.Equal(5, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task LengthTruncatedToolCallIsNeverExecuted()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.Length, new ToolCallContent("1", "write", "{}")),
            Responses.Text("recovered"));
        var executed = 0;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("write", (_, _, _) =>
        {
            Interlocked.Increment(ref executed);
            return new ValueTask<ToolResult>(Responses.Result("written"));
        }, ToolRisk.NonIdempotentWrite));
        var agent = new Agent(options);

        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(0, executed);
        var toolMessage = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.True(toolMessage.IsError);
        Assert.Contains("output limit", Assert.IsType<TextContent>(Assert.Single(toolMessage.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAndInvalidToolsAlwaysReceiveResults()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("missing", "missing", "{}"),
                new ToolCallContent("invalid", "typed", "{\"count\":\"wrong\"}")),
            Responses.Text("done"));
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "typed",
            (_, _, _) => new ValueTask<ToolResult>(Responses.Result("unexpected")),
            schema: "{\"type\":\"object\",\"required\":[\"count\"],\"properties\":{\"count\":{\"type\":\"integer\"}},\"additionalProperties\":false}"));
        var agent = new Agent(options);

        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        var toolMessages = agent.State.Messages.Where(message => message.Role == AgentRole.Tool).ToArray();
        Assert.Equal(2, toolMessages.Length);
        Assert.All(toolMessages, message => Assert.True(message.IsError));
        Assert.Equal(new[] { "missing", "invalid" }, toolMessages.Select(message => message.ToolCallId));
    }

    [Fact]
    public async Task UnsupportedSchemaAssertionsFailClosedBeforeToolExecution()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("ref", "referenced", "{}")),
            Responses.Text("done"));
        var executed = 0;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "referenced",
            (_, _, _) =>
            {
                executed++;
                return new ValueTask<ToolResult>(Responses.Result("unsafe"));
            },
            schema: "{\"$ref\":\"#/$defs/input\",\"$defs\":{\"input\":{\"type\":\"object\"}}}"));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(0, executed);
        var result = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.True(result.IsError);
        Assert.Contains("not supported", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSupportedSchemaAssertionsFailClosedBeforeToolExecution()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("invalid-schema", "write", "{\"value\":1.5}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("unsafe"));
            },
            schema: "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\",\"minimum\":\"zero\"}}}"));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.False(executed);
        var result = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.True(result.IsError);
        Assert.Contains("finite number", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedAssertionsInUnselectedSchemaBranchesStillFailClosed()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("nested-ref", "write", "{}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("unsafe"));
            },
            schema: "{\"anyOf\":[{\"type\":\"object\"},{\"$ref\":\"#/unsafe\"}]}"));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.False(executed);
        var result = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.Contains("not supported", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaConstAndEnumUseSemanticJsonEquality()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("semantic", "write", "{\"count\":1.0,\"choice\":{\"b\":2,\"a\":1}}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("ok"));
            },
            schema: "{\"type\":\"object\",\"required\":[\"count\",\"choice\"],\"properties\":{\"count\":{\"const\":1},\"choice\":{\"enum\":[{\"a\":1,\"b\":2}]}},\"additionalProperties\":false}"));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(executed);
        Assert.False(Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool).IsError);
    }

    [Fact]
    public async Task SchemaUniqueItemsUsesSemanticNumericEquality()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("duplicates", "write", "{\"values\":[1,1.0]}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("unsafe"));
            },
            schema: "{\"type\":\"object\",\"properties\":{\"values\":{\"type\":\"array\",\"uniqueItems\":true}},\"additionalProperties\":false}"));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.False(executed);
        var result = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.Contains("unique", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaNumberBoundsRetainPrecisionBeyondDoubleRange()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("precise", "write", "{\"value\":9007199254740993}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("unsafe"));
            },
            schema: "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\",\"minimum\":9007199254740994}}}"));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.False(executed);
        var result = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.Contains("smaller", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaIntegerAcceptsExactNumbersOutsideDecimalAndDoubleRanges()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("huge", "write", "{\"value\":1e1000}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("ok"));
            },
            schema: "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}}}"));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(executed);
    }

    [Theory]
    [InlineData("{}", "{\"allOf\":[]}", "at least one")]
    public async Task AmbiguousJsonAndSchemasFailClosed(
        string arguments,
        string schema,
        string expectedError)
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("ambiguous", "write", arguments)),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("unsafe"));
            },
            schema: schema));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.False(executed);
        var result = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.Contains(expectedError, Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejectedAtContractBoundaries()
    {
        Assert.Throws<ArgumentException>(() =>
            new ToolCallContent("call", "write", "{\"value\":1,\"value\":2}"));
        Assert.Throws<ArgumentException>(() =>
            new JsonContent("{\"nested\":{\"value\":1,\"value\":2}}"));
        Assert.Throws<ArgumentException>(() =>
            new ToolDefinition("write", "Write", "{\"type\":\"object\",\"type\":\"object\"}"));
    }

    [Fact]
    public async Task OversizedNumericArgumentsFailClosedBeforeToolExecution()
    {
        var number = "1" + new string('0', 4096);
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("large-number", "write", "{\"value\":" + number + "}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed = true;
                return new ValueTask<ToolResult>(Responses.Result("unsafe"));
            },
            schema: "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\"}}}"));

        var agent = new Agent(options);
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.False(executed);
        var result = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.Contains("4096-character", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParallelToolsEmitCompletionOrderButPersistSourceOrder()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("slow-id", "slow", "{}"),
                new ToolCallContent("fast-id", "fast", "{}")),
            Responses.Text("done"));
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("slow", async (_, _, cancellationToken) =>
        {
            await Task.Delay(100, cancellationToken);
            return Responses.Result("slow");
        }));
        options.Tools.Add(Responses.Tool("fast", async (_, _, cancellationToken) =>
        {
            await Task.Delay(5, cancellationToken);
            return Responses.Result("fast");
        }));
        var agent = new Agent(options);
        var completions = new List<string>();
        agent.Subscribe((value, _) =>
        {
            if (value.Kind == AgentEventKind.ToolEnded)
            {
                completions.Add(value.ToolCall!.Id);
            }

            return ValueTask.CompletedTask;
        });

        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "fast-id", "slow-id" }, completions);
        Assert.Equal(
            new[] { "slow-id", "fast-id" },
            agent.State.Messages.Where(message => message.Role == AgentRole.Tool).Select(message => message.ToolCallId));
    }

    [Fact]
    public async Task ParallelToolConcurrencyIsBounded()
    {
        var calls = Enumerable.Range(0, 8)
            .Select(index => new ToolCallContent(index.ToString(), "read", "{}"))
            .ToArray();
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, calls),
            Responses.Text("done"));
        var current = 0;
        var maximum = 0;
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxConcurrentTools = 2 },
        };
        options.Tools.Add(Responses.Tool("read", async (_, _, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref current);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximum);
            }
            while (active > observed && Interlocked.CompareExchange(ref maximum, active, observed) != observed);
            await Task.Delay(20, cancellationToken);
            Interlocked.Decrement(ref current);
            return Responses.Result("ok");
        }));
        var agent = new Agent(options);

        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(2, maximum);
    }

    [Fact]
    public async Task WriteToolForcesSafeParallelBatchToRunSequentially()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("1", "read", "{}"),
                new ToolCallContent("2", "write", "{}")),
            Responses.Text("done"));
        var current = 0;
        var maximum = 0;
        ValueTask<ToolResult> Execute()
        {
            var active = Interlocked.Increment(ref current);
            maximum = Math.Max(maximum, active);
            Thread.Sleep(20);
            Interlocked.Decrement(ref current);
            return new ValueTask<ToolResult>(Responses.Result("ok"));
        }

        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("read", (_, _, _) => Execute()));
        options.Tools.Add(Responses.Tool("write", (_, _, _) => Execute(), ToolRisk.IdempotentWrite));
        var agent = new Agent(options);

        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task ToolProgressAfterSettlementIsIgnored()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "work", "{}")),
            Responses.Text("done"));
        ToolExecutionContext? retained = null;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("work", (_, context, _) =>
        {
            retained = context;
            return new ValueTask<ToolResult>(Responses.Result("done"));
        }));
        var progress = 0;
        var agent = new Agent(options);
        agent.Subscribe((value, _) =>
        {
            if (value.Kind == AgentEventKind.ToolProgressed)
            {
                progress++;
            }

            return ValueTask.CompletedTask;
        });

        await agent.RunAsync("go", TestContext.Current.CancellationToken);
        await retained!.ReportProgressAsync(
            new ToolProgress("late"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, progress);
    }

    [Fact]
    public async Task SettledParallelToolProgressIsIgnoredWhileAnotherToolIsRunning()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("1", "fast", "{}"),
                new ToolCallContent("2", "slow", "{}")),
            Responses.Text("done"));
        var fastEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolExecutionContext? fastContext = null;
        var progress = 0;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("fast", (_, context, _) =>
        {
            fastContext = context;
            return new ValueTask<ToolResult>(Responses.Result("fast"));
        }, mode: ToolExecutionMode.Parallel));
        options.Tools.Add(Responses.Tool("slow", async (_, _, _) =>
        {
            slowStarted.TrySetResult();
            await releaseSlow.Task;
            return Responses.Result("slow");
        }, mode: ToolExecutionMode.Parallel));
        var agent = new Agent(options);
        agent.Subscribe((value, _) =>
        {
            if (value.Kind == AgentEventKind.ToolEnded && value.ToolCall?.Id == "1")
            {
                fastEnded.TrySetResult();
            }
            else if (value.Kind == AgentEventKind.ToolProgressed)
            {
                progress++;
            }

            return ValueTask.CompletedTask;
        });

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await Task.WhenAll(fastEnded.Task, slowStarted.Task)
            .WaitAsync(TestContext.Current.CancellationToken);
        await fastContext!.ReportProgressAsync(
            new ToolProgress(content: new AgentContent[] { new TextContent("late") }),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, progress);

        releaseSlow.TrySetResult();
        await run;
    }

    [Fact]
    public async Task AcceptedToolProgressSettlesBeforeToolEndEvenWhenToolDoesNotAwaitIt()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "work", "{}")),
            Responses.Text("done"));
        var progressStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<AgentEventKind>();
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("work", (_, context, _) =>
        {
            _ = context.ReportProgressAsync(new ToolProgress("working"));
            return new ValueTask<ToolResult>(Responses.Result("done"));
        }));
        var agent = new Agent(options);
        agent.Subscribe(async (value, _) =>
        {
            lock (events)
            {
                events.Add(value.Kind);
            }

            if (value.Kind == AgentEventKind.ToolProgressed)
            {
                progressStarted.TrySetResult();
                await releaseProgress.Task;
            }
        });

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await progressStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        lock (events)
        {
            Assert.DoesNotContain(AgentEventKind.ToolEnded, events);
        }

        releaseProgress.TrySetResult();
        await run;

        lock (events)
        {
            Assert.True(events.IndexOf(AgentEventKind.ToolProgressed) < events.IndexOf(AgentEventKind.ToolEnded));
        }
    }

    [Fact]
    public async Task OversizedToolProgressContentFailsTheToolBeforePublication()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "work", "{}")),
            Responses.Text("done"));
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxTextCharactersPerPart = 4 },
        };
        options.Tools.Add(Responses.Tool("work", async (_, context, cancellationToken) =>
        {
            await context.ReportProgressAsync(
                new ToolProgress(content: new AgentContent[] { new TextContent("too large") }),
                cancellationToken);
            return Responses.Result("done");
        }));
        var progressCount = 0;
        var agent = new Agent(options);
        agent.Subscribe((value, _) =>
        {
            if (value.Kind == AgentEventKind.ToolProgressed)
            {
                progressCount++;
            }

            return ValueTask.CompletedTask;
        });

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, progressCount);
        Assert.True(Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool).IsError);
    }

    [Fact]
    public async Task SteeringIsInjectedAfterCurrentToolBatch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "wait", "{}")),
            Responses.Text("done"));
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("wait", async (_, _, cancellationToken) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Responses.Result("released");
        }));
        var agent = new Agent(options);

        var run = agent.RunAsync("start", TestContext.Current.CancellationToken);
        await started.Task;
        agent.Steer("change course");
        release.SetResult();
        await run;

        var second = provider.Requests.ToArray()[1];
        Assert.Equal(
            new[] { AgentRole.User, AgentRole.Assistant, AgentRole.Tool, AgentRole.User },
            second.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task QueuedInputEventsBelongToTheTurnThatConsumesThem()
    {
        var first = Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("one", "read", "{}"));
        var second = Responses.Text("done");
        var provider = ScriptedProvider.FromResponses(first, second);
        var tool = Responses.Tool("read", (_, _, _) =>
            new ValueTask<ToolResult>(Responses.Result("ok")));
        var agent = new Agent(new AgentOptions(provider, "model")
        {
            Tools = { tool },
        });
        agent.Steer("new direction");
        var observed = new List<AgentEvent>();
        using var subscription = agent.Subscribe((agentEvent, _) =>
        {
            observed.Add(agentEvent);
            return ValueTask.CompletedTask;
        });

        var result = await agent.RunAsync("start", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var queuedMessageEvents = observed.Where(agentEvent =>
            (agentEvent.Kind is AgentEventKind.MessageStarted or AgentEventKind.MessageEnded)
            && agentEvent.Message?.Content.OfType<TextContent>().Any(content => content.Text == "new direction") == true)
            .ToArray();
        Assert.Equal(2, queuedMessageEvents.Length);
        Assert.All(queuedMessageEvents, agentEvent => Assert.Equal(1, agentEvent.Turn));
    }

    [Fact]
    public async Task FollowUpRunsAfterAgentWouldOtherwiseStop()
    {
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(async (call, _, cancellationToken) =>
        {
            if (call == 1)
            {
                firstRequest.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Responses.Text("first");
            }

            return Responses.Text("second");
        });
        var agent = new Agent(new AgentOptions(provider, "test"));

        var run = agent.RunAsync("start", TestContext.Current.CancellationToken);
        await firstRequest.Task;
        agent.FollowUp("again");
        release.SetResult();
        await run;

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(AgentRole.User, provider.Requests.ToArray()[1].Messages[^1].Role);
    }

    [Fact]
    public async Task AllTerminatingToolResultsStopTheModelLoop()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "finish", "{}")));
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("finish", (_, _, _) =>
            new ValueTask<ToolResult>(Responses.Result("finished", terminate: true))));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task MixedTerminatingToolResultsContinueTheModelLoop()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("finish", "finish", "{}"),
                new ToolCallContent("continue", "continue", "{}")),
            Responses.Text("done"));
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("finish", (_, _, _) =>
            new ValueTask<ToolResult>(Responses.Result("finished", terminate: true))));
        options.Tools.Add(Responses.Tool("continue", (_, _, _) =>
            new ValueTask<ToolResult>(Responses.Result("continue", terminate: false))));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task StopAfterTurnEndsGracefully()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("done"));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                ShouldStopAfterTurnAsync = (_, _) => new ValueTask<bool>(true),
            },
        };
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Stopped, result.Status);
    }

    [Fact]
    public async Task BlockedToolCanTerminateWithoutDispatching()
    {
        var executed = false;
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("blocked-1", "write", "{}")),
            Responses.Text("must not run"));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                BeforeToolCallAsync = (_, _) =>
                    new ValueTask<ToolCallDecision?>(ToolCallDecision.Block("denied", terminate: true)),
            },
        };
        options.Tools.Add(Responses.Tool("write", (_, _, _) =>
        {
            executed = true;
            return new ValueTask<ToolResult>(Responses.Result("unexpected"));
        }));

        var result = await new Agent(options).RunAsync("start", TestContext.Current.CancellationToken);

        Assert.False(executed);
        Assert.Equal(1, provider.CallCount);
        var toolResult = Assert.Single(result.NewMessages, message => message.Role == AgentRole.Tool);
        Assert.True(toolResult.IsError);
        Assert.Contains("denied", toolResult.Content.OfType<TextContent>().Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneTerminatingBlockedToolDoesNotStopAMixedBatch()
    {
        var executed = new List<string>();
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("blocked-1", "write", "{\"value\":\"first\"}"),
                new ToolCallContent("allowed-2", "write", "{\"value\":\"second\"}")),
            Responses.Text("done"));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                BeforeToolCallAsync = (context, _) =>
                    new ValueTask<ToolCallDecision?>(context.ToolCall.Id == "blocked-1"
                        ? ToolCallDecision.Block("denied", terminate: true)
                        : null),
            },
        };
        options.Tools.Add(Responses.Tool(
            "write",
            (arguments, _, _) =>
            {
                executed.Add(arguments.GetProperty("value").GetString()!);
                return new ValueTask<ToolResult>(Responses.Result("ok"));
            },
            schema: "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false}"));

        var result = await new Agent(options).RunAsync("start", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "second" }, executed);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(AgentRunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task NextTurnHookCanReplaceProviderModelAndContext()
    {
        var firstProvider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "next", "{}")));
        var secondProvider = ScriptedProvider.FromResponses(Responses.Text("done"));
        var replacement = AgentMessage.UserJson("{\"phase\":2}", DateTimeOffset.UnixEpoch);
        var options = new AgentOptions(firstProvider, "first")
        {
            Hooks = new AgentHooks
            {
                PrepareNextTurnAsync = (_, _) => new ValueTask<NextTurnUpdate?>(new NextTurnUpdate
                {
                    Provider = secondProvider,
                    Model = "second",
                    Context = new AgentContext("replacement", new[] { replacement }),
                }),
            },
        };
        options.Tools.Add(Responses.Tool("next", (_, _, _) => new ValueTask<ToolResult>(Responses.Result("ok"))));
        var agent = new Agent(options);

        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(1, firstProvider.CallCount);
        Assert.Equal(1, secondProvider.CallCount);
        var second = Assert.Single(secondProvider.Requests);
        Assert.Equal("second", second.Model);
        Assert.Equal("replacement", second.SystemPrompt);
        Assert.Single(second.Messages);
        Assert.IsType<JsonContent>(Assert.Single(second.Messages[0].Content));
    }

    [Fact]
    public async Task StopHookSeesContextPreparedForTheNextTurn()
    {
        var sawReplacement = false;
        var options = new AgentOptions(
            ScriptedProvider.FromResponses(
                Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "next", "{}"))),
            "test")
        {
            Hooks = new AgentHooks
            {
                PrepareNextTurnAsync = (_, _) => new ValueTask<NextTurnUpdate?>(
                    new NextTurnUpdate { Context = new AgentContext("replacement") }),
                ShouldStopAfterTurnAsync = (context, _) =>
                {
                    sawReplacement = context.Context.SystemPrompt == "replacement";
                    return new ValueTask<bool>(true);
                },
            },
        };
        options.Tools.Add(Responses.Tool("next", (_, _, _) => new ValueTask<ToolResult>(Responses.Result("ok"))));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Stopped, result.Status);
        Assert.True(sawReplacement);
    }

    [Fact]
    public async Task NextTurnProviderReplacementRequiresAnAtomicModelTarget()
    {
        var firstProvider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "next", "{}")));
        var secondProvider = ScriptedProvider.FromResponses(Responses.Text("unused"));
        var options = new AgentOptions(firstProvider, "first")
        {
            Hooks = new AgentHooks
            {
                PrepareNextTurnAsync = (_, _) => new ValueTask<NextTurnUpdate?>(new NextTurnUpdate
                {
                    Provider = secondProvider,
                }),
            },
        };
        options.Tools.Add(Responses.Tool("next", (_, _, _) => new ValueTask<ToolResult>(Responses.Result("ok"))));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.KernelError, result.Status);
        Assert.Equal(1, firstProvider.CallCount);
        Assert.Equal(0, secondProvider.CallCount);
        Assert.Contains("provider replacement", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringAfterToolHookDoesNotEraseCompletedToolResult()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("1", "commit", "{}")),
            Responses.Text("done"));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                AfterToolCallAsync = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return new ValueTask<ToolResult?>((ToolResult?)null);
                },
            },
        };
        options.Tools.Add(Responses.Tool(
            "commit",
            (_, _, _) =>
            {
                cancellation.Cancel();
                return new ValueTask<ToolResult>(Responses.Result("committed"));
            },
            ToolRisk.NonIdempotentWrite));
        var agent = new Agent(options);

        await agent.RunAsync("go", cancellation.Token);

        var toolMessage = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.False(toolMessage.IsError);
        Assert.Equal("committed", Assert.IsType<TextContent>(Assert.Single(toolMessage.Content)).Text);
    }

    [Fact]
    public async Task CancellationDuringToolPreparationSettlesEveryUndispatchedCall()
    {
        using var cancellation = new CancellationTokenSource();
        var executed = 0;
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("one", "write", "{}"),
                new ToolCallContent("two", "write", "{}")),
            Responses.Text("must not run"));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                BeforeToolCallAsync = (_, token) =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return new ValueTask<ToolCallDecision?>((ToolCallDecision?)null);
                },
            },
        };
        options.Tools.Add(Responses.Tool(
            "write",
            (_, _, _) =>
            {
                executed++;
                return new ValueTask<ToolResult>(Responses.Result("unexpected"));
            },
            ToolRisk.NonIdempotentWrite));
        var events = new List<AgentEventKind>();
        var agent = new Agent(options);
        agent.Subscribe((value, _) =>
        {
            events.Add(value.Kind);
            return ValueTask.CompletedTask;
        });

        var result = await agent.RunAsync("go", cancellation.Token);

        Assert.Equal(AgentRunStatus.Aborted, result.Status);
        Assert.Equal(0, executed);
        Assert.Equal(2, agent.State.Messages.Count(message => message.Role == AgentRole.Tool && message.IsError));
        Assert.Contains(AgentEventKind.TurnEnded, events);
        Assert.True(events.IndexOf(AgentEventKind.TurnEnded) < events.IndexOf(AgentEventKind.RunFaulted));
        AgentValidation.ValidateTranscript(agent.State.Messages, options.Limits);
    }

    [Fact]
    public async Task DuplicateToolCallIdsAreRejectedBeforeExecution()
    {
        var executions = 0;
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("duplicate", "read", "{}"),
                new ToolCallContent("duplicate", "read", "{}")));
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("read", (_, _, _) =>
        {
            executions++;
            return new ValueTask<ToolResult>(Responses.Result("unsafe"));
        }));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.ProviderError, result.Status);
        Assert.Equal(0, executions);
        Assert.DoesNotContain(
            agent.State.Messages.SelectMany(message => message.Content),
            content => content is ToolCallContent);
    }

    [Fact]
    public async Task TurnLimitClosesLifecycle()
    {
        var provider = new ScriptedProvider((call, _, _) => Task.FromResult(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent(call.ToString(), "again", "{}"))));
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxTurns = 2 },
        };
        options.Tools.Add(Responses.Tool("again", (_, _, _) => new ValueTask<ToolResult>(Responses.Result("ok"))));
        var agent = new Agent(options);
        var events = new List<AgentEventKind>();
        agent.Subscribe((value, _) =>
        {
            events.Add(value.Kind);
            return ValueTask.CompletedTask;
        });

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal(AgentEventKind.RunFaulted, events[^2]);
        Assert.Equal(AgentEventKind.RunEnded, events[^1]);
    }

    [Fact]
    public async Task ProviderExceptionBecomesNormalFailureLifecycle()
    {
        var provider = new ScriptedProvider((_, _, _) => throw new InvalidOperationException("offline"));
        var agent = new Agent(new AgentOptions(provider, "test"));

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.ProviderError, result.Status);
        Assert.Contains("offline", result.Error, StringComparison.Ordinal);
        Assert.Equal(AgentRole.Assistant, agent.State.Messages[^1].Role);
        Assert.Equal(ModelStopReason.Error, agent.State.Messages[^1].StopReason);
    }

    [Fact]
    public async Task StructuredProviderFailurePreservesDiagnostics()
    {
        var diagnostic = new ModelDiagnostic(
            "provider_failure",
            "Structured provider metadata is available.",
            ModelDiagnosticSeverity.Error,
            "{\"requestId\":\"request-1\"}");
        var provider = new ScriptedProvider((_, _, _) =>
            throw new ModelProviderException("offline", new[] { diagnostic }));
        var agent = new Agent(new AgentOptions(provider, "test"));

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.ProviderError, result.Status);
        var failure = agent.State.Messages[^1];
        Assert.Equal("provider_failure", Assert.Single(failure.Diagnostics).Code);
        Assert.Equal("{\"requestId\":\"request-1\"}", failure.Diagnostics[0].DataJson);
    }

    [Fact]
    public async Task SubscriberFailureDoesNotHideThePrimaryRunFailure()
    {
        var provider = new ScriptedProvider((_, _, _) => throw new InvalidOperationException("provider failed"));
        var agent = new Agent(new AgentOptions(provider, "test"));
        agent.Subscribe((_, _) => throw new InvalidOperationException("subscriber failed"));

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.ProviderError, result.Status);
        Assert.Contains("provider failed", result.Error, StringComparison.Ordinal);
        Assert.Contains("provider failed", agent.State.Error, StringComparison.Ordinal);
        Assert.Contains("subscriber failed", result.SubscriberErrors);
    }

    [Fact]
    public async Task ProviderFailureTextIsBoundedAndRetainsModelIdentity()
    {
        var provider = new ScriptedProvider((_, _, _) => throw new InvalidOperationException(new string('x', 256)));
        var options = new AgentOptions(provider, "bounded-model")
        {
            Limits = new AgentLimits { MaxTextCharactersPerPart = 32 },
        };
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.ProviderError, result.Status);
        Assert.Equal(32, result.Error!.Length);
        Assert.Equal("bounded-model", agent.State.Messages[^1].Model);
        Assert.Equal(32, agent.State.Messages[^1].ErrorMessage!.Length);
    }

    [Fact]
    public async Task HookAndSubscriberFailureTextIsBounded()
    {
        var options = new AgentOptions(ScriptedProvider.FromResponses(Responses.Text("unused")), "test")
        {
            Limits = new AgentLimits { MaxTextCharactersPerPart = 32 },
            Hooks = new AgentHooks
            {
                TransformContextAsync = (_, _) => throw new InvalidOperationException(new string('h', 256)),
            },
        };
        var agent = new Agent(options);
        agent.Subscribe((_, _) => throw new InvalidOperationException(new string('s', 256)));

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.KernelError, result.Status);
        Assert.Equal(32, result.Error!.Length);
        Assert.Equal(32, Assert.Single(result.SubscriberErrors).Length);
    }

    [Fact]
    public async Task AbortCancelsProviderAndSettles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(async (_, _, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Responses.Text("unreachable");
        });
        var agent = new Agent(new AgentOptions(provider, "test"));

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await entered.Task;
        agent.Abort();
        var result = await run;
        await agent.WaitForIdleAsync();

        Assert.Equal(AgentRunStatus.Aborted, result.Status);
        Assert.False(agent.State.IsRunning);
    }

    [Fact]
    public async Task AbortCannotBeBlockedByAThrowingCancellationCallback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(async (_, _, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(
                () => throw new InvalidOperationException("callback failed"));
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Responses.Text("unreachable");
        });
        var agent = new Agent(new AgentOptions(provider, "test"));

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(agent.TryAbort());
        var result = await run.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunStatus.Aborted, result.Status);
        Assert.False(agent.State.IsRunning);
    }

    [Fact]
    public async Task ModelTimeoutSettlesEvenWhenTheProviderIgnoresCancellation()
    {
        var provider = new NonCooperativeProvider();
        var agent = new Agent(new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { ModelTimeoutMilliseconds = 25 },
        });

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var result = await run.WaitAsync(TestContext.Current.CancellationToken);
        provider.Release.TrySetResult();

        Assert.Equal(AgentRunStatus.ProviderError, result.Status);
        Assert.Contains("exceeded 25 ms", result.Error, StringComparison.Ordinal);
        Assert.False(agent.State.IsRunning);
    }

    [Fact]
    public async Task AbortSettlesEvenWhenTheProviderIgnoresCancellation()
    {
        var provider = new NonCooperativeProvider();
        var agent = new Agent(new AgentOptions(provider, "test"));

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        agent.Abort();
        var result = await run.WaitAsync(TestContext.Current.CancellationToken);
        provider.Release.TrySetResult();

        Assert.Equal(AgentRunStatus.Aborted, result.Status);
        Assert.False(agent.State.IsRunning);
    }

    [Fact]
    public async Task LowLevelCancellationStillDeliversTerminalEvents()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(async (_, _, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Responses.Text("unreachable");
        });
        var events = new List<AgentEventKind>();
        var run = AgentLoop.RunAsync(
            new[] { AgentMessage.User("go", DateTimeOffset.UnixEpoch) },
            new AgentContext("system"),
            new AgentLoopOptions(provider, "test"),
            (value, callbackToken) =>
            {
                callbackToken.ThrowIfCancellationRequested();
                events.Add(value.Kind);
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await run;

        Assert.Equal(AgentRunStatus.Aborted, result.Status);
        Assert.Equal(AgentEventKind.RunFaulted, events[^2]);
        Assert.Equal(AgentEventKind.RunEnded, events[^1]);
    }

    [Fact]
    public async Task AbortIsSafeWhileRunsCompleteConcurrently()
    {
        var agent = new Agent(new AgentOptions(ScriptedProvider.FromResponses(
            Enumerable.Range(0, 256).Select(_ => Responses.Text("done")).ToArray()), "test"));

        for (var index = 0; index < 256; index++)
        {
            var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
            var aborts = Enumerable.Range(0, 8).Select(_ => Task.Run(agent.Abort)).ToArray();
            await Task.WhenAll(aborts.Append(run));
        }

        Assert.False(agent.State.IsRunning);
    }

    [Fact]
    public async Task AsyncSubscribersAreAwaitedAndFailuresAreReported()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("done"));
        var agent = new Agent(new AgentOptions(provider, "test"));
        var listenerFinished = false;
        agent.Subscribe(async (value, _) =>
        {
            if (value.Kind == AgentEventKind.RunEnded)
            {
                await Task.Delay(25);
                listenerFinished = true;
            }
        });
        agent.Subscribe((_, _) => throw new InvalidOperationException("listener failed"));

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(listenerFinished);
        Assert.Contains("listener failed", result.SubscriberErrors);
        Assert.Contains("subscribers failed", result.Error, StringComparison.Ordinal);
        Assert.False(result.Succeeded);
        Assert.False(agent.State.IsRunning);
    }

    [Fact]
    public async Task WaitForIdleIncludesRunEndedSubscriberSettlement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent(new AgentOptions(ScriptedProvider.FromResponses(Responses.Text("done")), "test"));
        agent.Subscribe(async (value, _) =>
        {
            if (value.Kind == AgentEventKind.RunEnded)
            {
                entered.TrySetResult();
                await release.Task;
            }
        });

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var idle = agent.WaitForIdleAsync();

        Assert.False(idle.IsCompleted);
        Assert.True(agent.State.IsRunning);
        release.TrySetResult();
        await Task.WhenAll(run, idle);
        Assert.False(agent.State.IsRunning);
    }

    [Fact]
    public async Task ActiveControlClosesBeforeRunEndedSubscribersSettle()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent(new AgentOptions(ScriptedProvider.FromResponses(Responses.Text("done")), "test"));
        agent.Subscribe(async (value, _) =>
        {
            if (value.Kind == AgentEventKind.RunEnded)
            {
                entered.TrySetResult();
                await release.Task;
            }
        });

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(agent.TrySteer("too late"));
        Assert.False(agent.TryAbort());
        release.TrySetResult();
        await run;
    }

    [Fact]
    public async Task StreamingStateExposesCurrentModelDeltaAndSettles()
    {
        static async IAsyncEnumerable<ModelStreamEvent> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partial = new ModelResponse(
                new AgentContent[] { new TextContent("hel") },
                ModelStopReason.Pending);
            yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, partial);
            yield return ModelStreamEvent.Update(ModelStreamEventKind.TextDelta, partial, "hel");
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(Responses.Text("hello"));
        }

        var agent = new Agent(new AgentOptions(new StreamingProvider((_, token) => Stream(token)), "test"));
        ModelStreamEvent? observed = null;
        var assistantEvents = new List<AgentEventKind>();
        agent.Subscribe((value, _) =>
        {
            if (value.Message?.Role == AgentRole.Assistant
                && value.Kind is AgentEventKind.MessageStarted or AgentEventKind.MessageUpdated or AgentEventKind.MessageEnded)
            {
                assistantEvents.Add(value.Kind);
            }

            if (value.Kind == AgentEventKind.MessageUpdated)
            {
                observed = agent.State.StreamingEvent;
                Assert.Equal(
                    "hel",
                    Assert.IsType<TextContent>(Assert.Single(agent.State.StreamingMessage!.Content)).Text);
            }

            return default;
        });

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(observed);
        Assert.Equal(ModelStreamEventKind.TextDelta, observed.Kind);
        Assert.Equal("hel", observed.Delta);
        Assert.Equal(
            new[] { AgentEventKind.MessageStarted, AgentEventKind.MessageUpdated, AgentEventKind.MessageEnded },
            assistantEvents);
        Assert.Null(agent.State.StreamingMessage);
        Assert.Null(agent.State.StreamingEvent);
    }

    [Fact]
    public async Task MessageCapacityStopsToolCallsBeforeAssistantIsCommitted()
    {
        var provider = ScriptedProvider.FromResponses(new ModelResponse(
            new AgentContent[]
            {
                new ToolCallContent("one", "write", "{}"),
                new ToolCallContent("two", "write", "{}"),
            },
            ModelStopReason.ToolUse));
        var executed = 0;
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxMessages = 3, MaxToolCallsPerTurn = 2 },
        };
        options.Tools.Add(new AgentTool(
            new ToolDefinition("write", "write", """{"type":"object"}"""),
            (_, _, _) =>
            {
                executed++;
                return new ValueTask<ToolResult>(new ToolResult(Array.Empty<AgentContent>()));
            },
            ToolRisk.NonIdempotentWrite));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal(0, executed);
        Assert.DoesNotContain(agent.State.Messages, message =>
            message.Content.Any(part => part is ToolCallContent));
    }

    [Fact]
    public async Task ProviderErrorClosesEveryCompleteToolCall()
    {
        var provider = ScriptedProvider.FromResponses(new ModelResponse(
            new AgentContent[] { new ToolCallContent("call", "write", "{}") },
            ModelStopReason.Error,
            errorMessage: "offline"));
        var executed = 0;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(new AgentTool(
            new ToolDefinition("write", "write", """{"type":"object"}"""),
            (_, _, _) =>
            {
                executed++;
                return new ValueTask<ToolResult>(new ToolResult(Array.Empty<AgentContent>()));
            }));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.ProviderError, result.Status);
        Assert.Equal(0, executed);
        var toolResult = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.Equal("call", toolResult.ToolCallId);
        Assert.True(toolResult.IsError);
        AgentValidation.ValidateTranscript(agent.State.Messages, options.Limits);
    }

    [Fact]
    public async Task BeforeToolHookOnlySeesSchemaValidArgumentsAndReplacementIsRevalidated()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("invalid-original", "write", "{}"),
                new ToolCallContent("invalid-replacement", "write", "{\"value\":1}")),
            Responses.Text("done"));
        var hookCalls = 0;
        var executed = 0;
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                BeforeToolCallAsync = (context, _) =>
                {
                    hookCalls++;
                    return new ValueTask<ToolCallDecision?>(
                        context.ToolCall.Id == "invalid-replacement"
                            ? ToolCallDecision.Allow("{}")
                            : ToolCallDecision.Allow());
                },
            },
        };
        options.Tools.Add(new AgentTool(
            new ToolDefinition(
                "write",
                "write",
                """{"type":"object","properties":{"value":{"type":"number"}},"required":["value"]}"""),
            (_, _, _) =>
            {
                executed++;
                return new ValueTask<ToolResult>(Responses.Result("ok"));
            }));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, hookCalls);
        Assert.Equal(0, executed);
        Assert.Equal(2, agent.State.Messages.Count(message => message.Role == AgentRole.Tool && message.IsError));
    }

    [Fact]
    public async Task ToolHooksReceiveAssistantMessageValidatedArgumentsAndRunCoordinates()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("call", "inspect", "{\"value\":7}")),
            Responses.Text("done"));
        BeforeToolCallContext? before = null;
        AfterToolCallContext? after = null;
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                BeforeToolCallAsync = (context, _) =>
                {
                    before = context;
                    return new ValueTask<ToolCallDecision?>((ToolCallDecision?)null);
                },
                AfterToolCallAsync = (context, _) =>
                {
                    after = context;
                    return new ValueTask<ToolResult?>((ToolResult?)null);
                },
            },
        };
        options.Tools.Add(Responses.Tool(
            "inspect",
            (_, _, _) => new ValueTask<ToolResult>(Responses.Result("ok")),
            schema: "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"]}"));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(result.RunId, before.RunId);
        Assert.Equal(1, before.Turn);
        Assert.Equal("call", before.ToolCall.Id);
        Assert.Equal(7, before.Arguments.GetProperty("value").GetInt32());
        Assert.Same(before.AssistantMessage, after.AssistantMessage);
        Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(after.Result.Content)).Text);
        Assert.Equal(AgentRole.Assistant, before.Context.Messages.Last().Role);
    }

    [Fact]
    public async Task LoopSnapshotsLimitsBeforeToolHooksCanMutateOptions()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("call", "write", "{}")),
            Responses.Text("done"));
        var executed = false;
        var options = new AgentLoopOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxJsonCharactersPerPart = 64 },
        };
        options.Hooks.BeforeToolCallAsync = (_, _) =>
        {
            options.Limits.MaxJsonCharactersPerPart = 1;
            return new ValueTask<ToolCallDecision?>(ToolCallDecision.Allow("{}"));
        };
        var context = new AgentContext(
            string.Empty,
            tools: new[]
            {
                Responses.Tool("write", (_, _, _) =>
                {
                    executed = true;
                    return new ValueTask<ToolResult>(Responses.Result("ok"));
                }),
            });

        var result = await AgentLoop.RunAsync(
            new[] { AgentMessage.User("go", DateTimeOffset.UnixEpoch) },
            context,
            options,
            (_, _) => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(executed);
    }

    [Fact]
    public async Task PublicAgentLoopSerializesEventsFromParallelTools()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("one", "inspect_one", "{}"),
                new ToolCallContent("two", "inspect_two", "{}")),
            Responses.Text("done"));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        AgentTool Tool(string name) => Responses.Tool(name, async (_, execution, cancellationToken) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                ready.TrySetResult();
            }

            await ready.Task.WaitAsync(cancellationToken);
            await execution.ReportProgressAsync(new ToolProgress(name), cancellationToken);
            return Responses.Result("ok");
        });
        var context = new AgentContext(
            string.Empty,
            tools: new[] { Tool("inspect_one"), Tool("inspect_two") });
        var activeCallbacks = 0;
        var concurrentCallback = 0;

        var result = await AgentLoop.RunAsync(
            new[] { AgentMessage.User("go", DateTimeOffset.UnixEpoch) },
            context,
            new AgentLoopOptions(provider, "test"),
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref activeCallbacks) > 1)
                {
                    Interlocked.Exchange(ref concurrentCallback, 1);
                }

                await Task.Delay(5, cancellationToken);
                Interlocked.Decrement(ref activeCallbacks);
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, concurrentCallback);
    }

    [Fact]
    public async Task ContinueFromUserAddsOnlyNewAssistantMessages()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("continued"));
        var initial = AgentMessage.User("already stored", DateTimeOffset.UnixEpoch);
        var options = new AgentOptions(provider, "test");
        options.InitialMessages.Add(initial);
        var agent = new Agent(options);
        var messageEnds = new List<AgentMessage>();
        agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent.Kind == AgentEventKind.MessageEnded && agentEvent.Message is not null)
            {
                messageEnds.Add(agentEvent.Message);
            }

            return default;
        });

        var result = await agent.ContinueAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Single(result.NewMessages);
        Assert.Equal(AgentRole.Assistant, result.NewMessages[0].Role);
        Assert.Single(messageEnds);
        Assert.Same(initial, agent.State.Messages[0]);
    }

    [Fact]
    public async Task ContinueAcceptsCustomTailAsCallerOwnedContext()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("continued"));
        var options = new AgentOptions(provider, "test");
        options.InitialMessages.Add(new AgentMessage(
            AgentRole.Custom,
            new AgentContent[] { new JsonContent("{\"event\":\"month_elapsed\"}") },
            DateTimeOffset.UnixEpoch,
            customRole: "game_event"));
        var agent = new Agent(options);

        var result = await agent.ContinueAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentRole.Custom, Assert.Single(provider.Requests).Messages[0].Role);
    }

    [Fact]
    public async Task ToolStartSubscriberSeesPendingCallAndStableSourceIndex()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("call", "inspect", "{}")),
            Responses.Text("done"));
        var observedPending = false;
        var observedIndex = -1;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(Responses.Tool("inspect", (_, execution, _) =>
        {
            observedIndex = execution.ToolCallIndex;
            return new ValueTask<ToolResult>(Responses.Result("ok"));
        }));
        var agent = new Agent(options);
        agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent.Kind == AgentEventKind.ToolStarted)
            {
                observedPending = agent.State.PendingToolCallIds.Contains("call", StringComparer.Ordinal);
            }

            return default;
        });

        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(observedPending);
        Assert.Equal(0, observedIndex);
        Assert.Empty(agent.State.PendingToolCallIds);
    }

    [Fact]
    public async Task SubscribersReceiveTheActiveCancelableRunToken()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Responses.Text("unreachable");
        });
        var agent = new Agent(new AgentOptions(provider, "test"));
        var canBeCanceled = false;
        var terminalObservedCancellation = false;
        agent.Subscribe((agentEvent, cancellationToken) =>
        {
            if (agentEvent.Kind == AgentEventKind.RunStarted)
            {
                canBeCanceled = cancellationToken.CanBeCanceled;
                entered.TrySetResult();
            }
            else if (agentEvent.Kind == AgentEventKind.RunEnded)
            {
                terminalObservedCancellation = cancellationToken.IsCancellationRequested;
            }

            return default;
        });

        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        agent.Abort();
        var result = await run;

        Assert.True(canBeCanceled);
        Assert.True(terminalObservedCancellation);
        Assert.Equal(AgentRunStatus.Aborted, result.Status);
    }

    [Fact]
    public async Task ToolCanNormalizeCompatibilityArgumentsBeforeSchemaValidation()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("edit-call", "edit", "{\"oldText\":\"before\",\"newText\":\"after\"}")),
            Responses.Text("done"));
        string? executed = null;
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(new AgentTool(
            new ToolDefinition(
                "edit",
                "edit",
                "{\"type\":\"object\",\"required\":[\"edits\"],\"properties\":{\"edits\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"required\":[\"oldText\",\"newText\"]}}}}"),
            (arguments, _, _) =>
            {
                executed = arguments.GetProperty("edits")[0].GetProperty("newText").GetString();
                return new ValueTask<ToolResult>(Responses.Result("edited"));
            },
            prepareArguments: arguments => JsonSerializer.Serialize(new
            {
                edits = new[]
                {
                    new
                    {
                        oldText = arguments.GetProperty("oldText").GetString(),
                        newText = arguments.GetProperty("newText").GetString(),
                    },
                },
            })));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("after", executed);
    }

    [Fact]
    public async Task ContinueValidatesEmptyAndAssistantTailButConsumesQueuedInputsOneAtATime()
    {
        var empty = new Agent(new AgentOptions(ScriptedProvider.FromResponses(Responses.Text("unused")), "test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            empty.ContinueAsync(TestContext.Current.CancellationToken));

        var provider = ScriptedProvider.FromResponses(Responses.Text("one"), Responses.Text("two"));
        var options = new AgentOptions(provider, "test");
        options.InitialMessages.Add(new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[] { new TextContent("tail") },
            DateTimeOffset.UnixEpoch,
            model: "test",
            stopReason: ModelStopReason.Stop));
        var agent = new Agent(options);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.ContinueAsync(TestContext.Current.CancellationToken));

        agent.Steer("first");
        agent.Steer("second");
        var result = await agent.ContinueAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(
            new[] { AgentRole.User, AgentRole.Assistant, AgentRole.User, AgentRole.Assistant },
            agent.State.Messages.Skip(1).Select(message => message.Role));
    }

    [Fact]
    public async Task ContinueFromFollowUpStillPollsNewSteeringBeforeTheFirstModelRequest()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("done"));
        var options = new AgentOptions(provider, "test");
        options.InitialMessages.Add(new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[] { new TextContent("waiting") },
            DateTimeOffset.UnixEpoch,
            model: "test",
            stopReason: ModelStopReason.Stop));
        var agent = new Agent(options);
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = agent.Subscribe(async (agentEvent, cancellationToken) =>
        {
            if (agentEvent.Kind == AgentEventKind.RunStarted)
            {
                runStarted.TrySetResult();
                await releaseRun.Task.WaitAsync(cancellationToken);
            }
        });
        agent.FollowUp("queued follow-up");

        var run = agent.ContinueAsync(TestContext.Current.CancellationToken);
        await runStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var steeringAccepted = agent.TrySteer("urgent steering");
        releaseRun.TrySetResult();
        Assert.True(steeringAccepted);
        await run;

        var request = Assert.Single(provider.Requests);
        Assert.Equal(
            new[] { "waiting", "queued follow-up", "urgent steering" },
            request.Messages.Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));
    }

    [Fact]
    public async Task ExplicitSequentialToolOverridesParallelBatch()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("one", "first", "{}"),
                new ToolCallContent("two", "second", "{}")),
            Responses.Text("done"));
        var active = 0;
        var maximum = 0;
        async ValueTask<ToolResult> Execute()
        {
            var value = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, value);
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Interlocked.Decrement(ref active);
            return Responses.Result("ok");
        }

        var options = new AgentOptions(provider, "test") { ToolExecution = ToolExecutionMode.Parallel };
        options.Tools.Add(Responses.Tool("first", (_, _, _) => Execute(), mode: ToolExecutionMode.Sequential));
        options.Tools.Add(Responses.Tool("second", (_, _, _) => Execute(), mode: ToolExecutionMode.Parallel));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task SafeParallelConflictKeysSerializeOnlyConflictingWrites()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(
                ModelStopReason.ToolUse,
                new ToolCallContent("one", "write", "{\"target\":\"same\"}"),
                new ToolCallContent("two", "write", "{\"target\":\"same\"}"),
                new ToolCallContent("three", "write", "{\"target\":\"other\"}")),
            Responses.Text("done"));
        var sameActive = 0;
        var sameMaximum = 0;
        var allActive = 0;
        var allMaximum = 0;
        var metricGate = new object();
        async ValueTask<ToolResult> Execute(System.Text.Json.JsonElement arguments)
        {
            var target = arguments.GetProperty("target").GetString();
            lock (metricGate)
            {
                allActive++;
                allMaximum = Math.Max(allMaximum, allActive);
                if (target == "same")
                {
                    sameActive++;
                    sameMaximum = Math.Max(sameMaximum, sameActive);
                }
            }

            await Task.Delay(30, TestContext.Current.CancellationToken);
            lock (metricGate)
            {
                if (target == "same") sameActive--;
                allActive--;
            }

            return Responses.Result("ok");
        }

        var tool = new AgentTool(
            new ToolDefinition(
                "write",
                "write",
                """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}"""),
            (arguments, _, _) => Execute(arguments),
            ToolRisk.IdempotentWrite,
            ToolExecutionMode.Parallel,
            conflictKey: arguments => arguments.GetProperty("target").GetString());
        var options = new AgentOptions(provider, "test");
        options.Tools.Add(tool);
        var agent = new Agent(options);
        var turnEnds = new List<AgentEvent>();
        agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent.Kind == AgentEventKind.TurnEnded)
            {
                turnEnds.Add(agentEvent);
            }

            return default;
        });

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, sameMaximum);
        Assert.True(allMaximum >= 2);
        Assert.Equal(new[] { "one", "two", "three" }, turnEnds[0].Messages.Select(message => message.ToolCallId));
    }

    [Fact]
    public async Task ConcurrentRunIsRejected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(async (_, _, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Responses.Text("done");
        });
        var agent = new Agent(new AgentOptions(provider, "test"));

        var first = agent.RunAsync("one", TestContext.Current.CancellationToken);
        await entered.Task;
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = agent.RunAsync("two", TestContext.Current.CancellationToken);
        });
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task ResetIsRejectedDuringARunWithoutCorruptingTranscript()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(async (_, _, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Responses.Text("done");
        });
        var agent = new Agent(new AgentOptions(provider, "test"));

        var run = agent.RunAsync("one", TestContext.Current.CancellationToken);
        await entered.Task;

        Assert.Throws<InvalidOperationException>(() => agent.Reset());
        Assert.True(agent.State.IsRunning);
        Assert.Single(agent.State.Messages);

        release.SetResult();
        await run;

        Assert.False(agent.State.IsRunning);
        Assert.Equal(
            new[] { AgentRole.User, AgentRole.Assistant },
            agent.State.Messages.Select(message => message.Role));
    }

    [Fact]
    public void QueuesAreBounded()
    {
        var options = new AgentOptions(ScriptedProvider.FromResponses(Responses.Text("done")), "test")
        {
            Limits = new AgentLimits { MaxQueuedMessages = 1 },
        };
        var agent = new Agent(options);

        agent.Steer("one");

        Assert.Throws<InvalidOperationException>(() => agent.Steer("two"));
    }

    [Fact]
    public async Task TokenBudgetStopsBeforeToolSideEffectsAndClosesToolCalls()
    {
        var provider = ScriptedProvider.FromResponses(new ModelResponse(
            new AgentContent[] { new ToolCallContent("expensive", "write", "{}") },
            ModelStopReason.ToolUse,
            new ModelUsage(8, 8)));
        var executed = 0;
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxTotalTokens = 10 },
        };
        options.Tools.Add(Responses.Tool("write", (_, _, _) =>
        {
            Interlocked.Increment(ref executed);
            return new ValueTask<ToolResult>(Responses.Result("written"));
        }, ToolRisk.NonIdempotentWrite));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal(0, executed);
        var tool = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.True(tool.IsError);
        Assert.Contains("token budget", Assert.IsType<TextContent>(Assert.Single(tool.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolReportedUsageCountsTowardBudgetAndRunUsage()
    {
        var provider = ScriptedProvider.FromResponses(new ModelResponse(
            new AgentContent[] { new ToolCallContent("nested", "think", "{}") },
            ModelStopReason.ToolUse,
            new ModelUsage(1, 1)));
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxTotalTokens = 10 },
        };
        options.Tools.Add(Responses.Tool("think", (_, _, _) =>
            new ValueTask<ToolResult>(new ToolResult(
                new AgentContent[] { new TextContent("nested result") },
                usage: new ModelUsage(5, 4)))));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal(11, result.Usage.TotalTokens);
        Assert.Single(provider.Requests);
        Assert.Equal(9, Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool).Usage!.TotalTokens);
    }

    [Fact]
    public async Task RunUsageCanReportTheFinalIncrementBeyondTheConfiguredBudget()
    {
        var provider = ScriptedProvider.FromResponses(new ModelResponse(
            new AgentContent[] { new ToolCallContent("call", "costly", "{}") },
            ModelStopReason.ToolUse,
            new ModelUsage(6_000_000_000)));
        var agent = new Agent(new AgentOptions(provider, "model")
        {
            Limits = new AgentLimits { MaxTotalTokens = 10_000_000_000 },
            Tools =
            {
                new AgentTool(
                    new ToolDefinition("costly", "Reports usage", "{\"type\":\"object\"}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                        new AgentContent[] { new TextContent("done") },
                        usage: new ModelUsage(6_000_000_000)))),
            },
        });

        var result = await agent.RunAsync(AgentMessage.User("run"), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal(12_000_000_000, result.Usage.TotalTokens);
    }

    [Fact]
    public void StreamAndUsageContractsRejectAmbiguousTerminalState()
    {
        var pending = new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending);
        var complete = new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Stop);

        Assert.Throws<ArgumentException>(() => ModelStreamEvent.Terminal(pending));
        Assert.Throws<ArgumentException>(() => ModelStreamEvent.Update(ModelStreamEventKind.Started, complete));
        Assert.Throws<ArgumentException>(() => ModelStreamEvent.Update(ModelStreamEventKind.TextDelta, pending));
        var toolCall = new ToolCallContent(
            "call-1",
            "inspect",
            "{\"depth\":2}",
            thoughtSignature: "opaque",
            toolNamespace: "world");
        Assert.Throws<ArgumentException>(() =>
            ModelStreamEvent.Update(ModelStreamEventKind.ToolCallEnded, pending));
        Assert.Throws<ArgumentException>(() =>
            ModelStreamEvent.Update(ModelStreamEventKind.TextEnded, pending, toolCall: toolCall));
        Assert.Throws<ArgumentException>(() =>
            ModelStreamEvent.Update(ModelStreamEventKind.TextEnded, pending));
        Assert.Throws<ArgumentException>(() =>
            ModelStreamEvent.Update(ModelStreamEventKind.Started, pending, content: "unexpected"));
        var textPartial = new ModelResponse(
            new AgentContent[] { new TextContent("complete text") },
            ModelStopReason.Pending);
        var textEnded = ModelStreamEvent.Update(
            ModelStreamEventKind.TextEnded,
            textPartial,
            content: "complete text");
        Assert.Equal("complete text", textEnded.Content);
        Assert.Equal(0, textEnded.ContentIndex);
        Assert.Throws<ArgumentException>(() =>
            ModelStreamEvent.Update(
                ModelStreamEventKind.TextEnded,
                textPartial,
                content: "different"));
        var toolPartial = new ModelResponse(new AgentContent[] { toolCall }, ModelStopReason.Pending);
        Assert.Throws<ArgumentException>(() =>
            ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallEnded,
                toolPartial,
                toolCallId: "different",
                toolCall: toolCall));
        Assert.Throws<ArgumentException>(() =>
            ModelStreamEvent.Update(
                ModelStreamEventKind.ToolCallEnded,
                toolPartial,
                toolName: "different",
                toolCall: toolCall));
        var toolEnded = ModelStreamEvent.Update(
            ModelStreamEventKind.ToolCallEnded,
            toolPartial,
            toolCall: toolCall);
        Assert.Same(toolCall, toolEnded.ToolCall);
        Assert.Equal(toolCall.Id, toolEnded.ToolCallId);
        Assert.Equal(toolCall.Name, toolEnded.ToolName);
        Assert.Equal(0, toolEnded.ContentIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelUsage(10_000_000_001));
        Assert.Throws<ArgumentException>(() => new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Stop,
            errorMessage: "unexpected"));
    }

    [Fact]
    public async Task UncooperativeToolTimesOutWithUncertainOutcome()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("slow", "write", "{}")),
            Responses.Text("continued"));
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { ToolTimeoutMilliseconds = 50 },
        };
        options.Tools.Add(Responses.Tool("write", async (_, _, _) =>
        {
            await release.Task;
            return Responses.Result("late");
        }, ToolRisk.NonIdempotentWrite));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);
        release.TrySetResult();

        Assert.True(result.Succeeded);
        var tool = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.True(tool.IsError);
        Assert.Contains("outcome is uncertain", Assert.IsType<TextContent>(Assert.Single(tool.Content)).Text, StringComparison.Ordinal);
        Assert.Equal("{\"outcome\":\"uncertain\"}", tool.DetailsJson);
    }

    [Fact]
    public async Task AbortSettlesBeforeAnUncooperativeWriteFinishesLate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("slow", "write", "{}")));
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { ToolTimeoutMilliseconds = 5_000 },
        };
        options.Tools.Add(Responses.Tool("write", async (_, _, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(
                () => throw new InvalidOperationException("cancellation callback failed"));
            entered.TrySetResult();
            return await release.Task.ConfigureAwait(false);
        }, ToolRisk.NonIdempotentWrite));
        var agent = new Agent(options);

        var pending = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        agent.Abort();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        release.TrySetException(new InvalidOperationException("late failure"));

        Assert.Equal(AgentRunStatus.Aborted, result.Status);
        var tool = Assert.Single(result.NewMessages, message => message.Role == AgentRole.Tool);
        Assert.True(tool.IsError);
        Assert.Equal("{\"outcome\":\"uncertain\"}", tool.DetailsJson);
    }

    [Fact]
    public async Task UncertainSequentialWritePreventsLaterWritesInTheBatch()
    {
        var firstExecutions = 0;
        var secondExecutions = 0;
        var provider = ScriptedProvider.FromResponses(Responses.Tools(
            ModelStopReason.ToolUse,
            new ToolCallContent("first", "first_write", "{}"),
            new ToolCallContent("second", "second_write", "{}")));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                ShouldStopAfterTurnAsync = (_, _) => new ValueTask<bool>(true),
            },
        };
        options.Tools.Add(Responses.Tool("first_write", (_, _, _) =>
        {
            firstExecutions++;
            throw new InvalidOperationException("failed after dispatch");
        }, ToolRisk.NonIdempotentWrite));
        options.Tools.Add(Responses.Tool("second_write", (_, _, _) =>
        {
            secondExecutions++;
            return new ValueTask<ToolResult>(Responses.Result("unexpected"));
        }, ToolRisk.NonIdempotentWrite));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Stopped, result.Status);
        Assert.Equal(1, firstExecutions);
        Assert.Equal(0, secondExecutions);
        var toolResults = result.NewMessages.Where(message => message.Role == AgentRole.Tool).ToArray();
        Assert.Equal("{\"outcome\":\"uncertain\"}", toolResults[0].DetailsJson);
        Assert.Contains("not executed", Assert.IsType<TextContent>(Assert.Single(toolResults[1].Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncertainParallelWritePoisonsItsConflictKeyForWaitingCalls()
    {
        var executions = new int[2];
        var provider = ScriptedProvider.FromResponses(Responses.Tools(
            ModelStopReason.ToolUse,
            new ToolCallContent("first", "write", "{}"),
            new ToolCallContent("second", "write", "{}")));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                ShouldStopAfterTurnAsync = (_, _) => new ValueTask<bool>(true),
            },
        };
        options.Tools.Add(new AgentTool(
            new ToolDefinition("write", "Write", "{\"type\":\"object\"}"),
            (_, execution, _) =>
            {
                executions[execution.ToolCallIndex]++;
                if (execution.ToolCallIndex == 0)
                {
                    throw new InvalidOperationException("failed after dispatch");
                }

                return new ValueTask<ToolResult>(Responses.Result("unexpected"));
            },
            ToolRisk.IdempotentWrite,
            ToolExecutionMode.Parallel,
            conflictKey: _ => "world-region"));

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Stopped, result.Status);
        Assert.Equal(new[] { 1, 0 }, executions);
        var second = Assert.Single(result.NewMessages, message => message.ToolCallId == "second");
        Assert.Contains("same conflict key", Assert.IsType<TextContent>(Assert.Single(second.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeforeModelHookCannotBypassRequestLimits()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("unused"));
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxSystemPromptCharacters = 8 },
            Hooks = new AgentHooks
            {
                BeforeModelRequestAsync = (request, _) => new ValueTask<ModelRequest>(new ModelRequest(
                    request.Model,
                    "this prompt is too large",
                    request.Messages,
                    request.Tools,
                    request.Parameters,
                    request.SessionId,
                    request.RunId,
                    request.Turn)),
            },
        };
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.LimitExceeded, result.Status);
        Assert.Equal(0, provider.CallCount);
        Assert.Contains("system prompt", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BeforeModelHookReplacementModelIsUsedForDispatchAndMessageIdentity()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("done"));
        var options = new AgentOptions(provider, "original")
        {
            Hooks = new AgentHooks
            {
                BeforeModelRequestAsync = (request, _) => new ValueTask<ModelRequest>(new ModelRequest(
                    "replacement",
                    request.SystemPrompt,
                    request.Messages,
                    request.Tools,
                    request.Parameters,
                    request.SessionId,
                    request.RunId,
                    request.Turn)),
            },
        };
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("replacement", Assert.Single(provider.Requests).Model);
        Assert.Equal("replacement", agent.State.Messages.Last().Model);
        Assert.Equal("replacement", result.NewMessages.Last().Model);
    }

    [Fact]
    public async Task ProviderDisposalFailureAfterTerminalCannotReplaceACompletedResponse()
    {
        var events = new ConcurrentQueue<AgentEvent>();
        var agent = new Agent(new AgentOptions(new TerminalThenDisposalFailureProvider(), "model"));
        using var subscription = agent.Subscribe((agentEvent, _) =>
        {
            events.Enqueue(agentEvent);
            return default;
        });

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, events.Count(value => value.Kind == AgentEventKind.MessageEnded
            && value.Message?.Role == AgentRole.Assistant));
        Assert.Equal(1, events.Count(value => value.Kind == AgentEventKind.RunEnded));
        Assert.DoesNotContain(events, value => value.Kind == AgentEventKind.RunFaulted);
    }

    [Fact]
    public async Task BeforeModelHookCannotChangeActiveRunCoordinates()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("unused"));
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                BeforeModelRequestAsync = (request, _) => new ValueTask<ModelRequest>(new ModelRequest(
                    request.Model,
                    request.SystemPrompt,
                    request.Messages,
                    request.Tools,
                    request.Parameters,
                    request.SessionId,
                    "different-run",
                    request.Turn + 1)),
            },
        };

        var result = await new Agent(options).RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.KernelError, result.Status);
        Assert.Equal(0, provider.CallCount);
        Assert.Contains("run ID or turn", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdleModelParametersCanBeReplacedWithoutSharingMutableState()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("done"));
        var agent = new Agent(new AgentOptions(provider, "test"));
        var parameters = new ModelParameters
        {
            Temperature = 0.25,
            Extensions = new Dictionary<string, string> { ["mode"] = "\"game\"" },
        };

        agent.SetModelParameters(parameters);
        parameters.Temperature = 0.9;
        await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(0.25, Assert.Single(provider.Requests).Parameters.Temperature);
        Assert.Equal(0.25, agent.State.Parameters.Temperature);
        Assert.Equal("\"game\"", agent.State.Parameters.Extensions["mode"]);
    }

    [Fact]
    public async Task IdleAgentCanReplaceSessionAffinityBetweenRuns()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("first"), Responses.Text("second"));
        var agent = new Agent(new AgentOptions(provider, "test") { SessionId = "session-one" });

        await agent.RunAsync("one", TestContext.Current.CancellationToken);
        agent.SetSessionId("session-two");
        await agent.RunAsync("two", TestContext.Current.CancellationToken);

        Assert.Equal("session-two", agent.SessionId);
        Assert.Equal("session-two", agent.State.SessionId);
        Assert.Equal(new[] { "session-one", "session-two" }, provider.Requests.Select(request => request.SessionId));
    }

    [Fact]
    public async Task IdleAgentCanAtomicallySwitchProviderAndModelBetweenRuns()
    {
        var firstProvider = ScriptedProvider.FromResponses(Responses.Text("first"));
        var secondProvider = ScriptedProvider.FromResponses(Responses.Text("second"));
        var agent = new Agent(new AgentOptions(firstProvider, "first-model"));

        await agent.RunAsync("one", TestContext.Current.CancellationToken);
        agent.SetModel(secondProvider, "second-model");
        await agent.RunAsync("two", TestContext.Current.CancellationToken);

        Assert.Same(secondProvider, agent.State.Provider);
        Assert.Equal("second-model", agent.State.Model);
        Assert.Equal(1, firstProvider.CallCount);
        Assert.Equal(1, secondProvider.CallCount);
        Assert.Equal("second-model", secondProvider.Requests.Single().Model);
    }

    [Fact]
    public async Task IdleAgentCanReplaceHooksBetweenRunsWithoutSharingMutableState()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("first"), Responses.Text("second"));
        var agent = new Agent(new AgentOptions(provider, "test"));

        await agent.RunAsync("one", TestContext.Current.CancellationToken);
        var hooks = new AgentHooks
        {
            BeforeModelRequestAsync = (request, _) => new ValueTask<ModelRequest>(new ModelRequest(
                "replacement",
                request.SystemPrompt,
                request.Messages,
                request.Tools,
                request.Parameters,
                request.SessionId,
                request.RunId,
                request.Turn)),
        };
        agent.SetHooks(hooks);
        hooks.BeforeModelRequestAsync = null;

        await agent.RunAsync("two", TestContext.Current.CancellationToken);

        Assert.Equal("replacement", provider.Requests.Last().Model);
    }

    [Fact]
    public async Task MutableRuntimeConfigurationCannotChangeDuringAnActiveRun()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Responses.Text("done");
        });
        var agent = new Agent(new AgentOptions(provider, "test"));
        var run = agent.RunAsync("go", TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => agent.SetHooks(new AgentHooks()));
        Assert.Throws<InvalidOperationException>(() => agent.SetToolExecution(ToolExecutionMode.Sequential));
        Assert.Throws<InvalidOperationException>(() => agent.SetSessionId("other"));

        release.TrySetResult();
        Assert.True((await run).Succeeded);
    }

    [Fact]
    public async Task OversizedAfterToolHookResultBecomesBoundedToolError()
    {
        var provider = ScriptedProvider.FromResponses(
            Responses.Tools(ModelStopReason.ToolUse, new ToolCallContent("call", "look", "{}")),
            Responses.Text("done"));
        var options = new AgentOptions(provider, "test")
        {
            Limits = new AgentLimits { MaxTextCharactersPerPart = 64 },
            Hooks = new AgentHooks
            {
                AfterToolCallAsync = (_, _) => new ValueTask<ToolResult?>(
                    new ToolResult(new AgentContent[] { new TextContent(new string('x', 100)) })),
            },
        };
        options.Tools.Add(Responses.Tool("look", (_, _, _) =>
            new ValueTask<ToolResult>(Responses.Result("ok"))));
        var agent = new Agent(options);

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var tool = Assert.Single(agent.State.Messages, message => message.Role == AgentRole.Tool);
        Assert.True(tool.IsError);
        var error = Assert.IsType<TextContent>(Assert.Single(tool.Content)).Text;
        Assert.Contains("Tool result rejected", error, StringComparison.Ordinal);
        Assert.True(error.Length <= 64);
    }

    [Fact]
    public void FloatingPointConfigurationRejectsNonFiniteValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolProgress(fraction: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolProgress(fraction: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new AgentOptions(
            ScriptedProvider.FromResponses(Responses.Text("unused")),
            "test")
        {
            Parameters = new ModelParameters { Temperature = double.NaN },
        }));
    }

    [Fact]
    public void ToolProgressContentIsValidatedAndDefensivelyCopied()
    {
        var source = new AgentContent[] { new TextContent("preview") };
        var progress = new ToolProgress(content: source);
        source[0] = new TextContent("changed");

        Assert.Equal("preview", Assert.IsType<TextContent>(Assert.Single(progress.Content)).Text);
        Assert.Throws<ArgumentException>(() => new ToolProgress(
            content: new AgentContent[] { new ReasoningContent("private") }));
    }

    [Fact]
    public async Task PublicSnapshotsAndLifecycleCollectionsAreImmutableDefensiveCopies()
    {
        var provider = ScriptedProvider.FromResponses(Responses.Text("done"));
        AfterTurnContext? afterTurn = null;
        AgentEvent? ended = null;
        var options = new AgentOptions(provider, "test")
        {
            Hooks = new AgentHooks
            {
                ShouldStopAfterTurnAsync = (value, _) =>
                {
                    afterTurn = value;
                    return new ValueTask<bool>(false);
                },
            },
        };
        options.Tools.Add(Responses.Tool("look", (_, _, _) =>
            new ValueTask<ToolResult>(Responses.Result("ok"))));
        var agent = new Agent(options);
        using var subscription = agent.Subscribe((value, _) =>
        {
            if (value.Kind == AgentEventKind.RunEnded)
            {
                ended = value;
            }

            return ValueTask.CompletedTask;
        });

        var result = await agent.RunAsync("go", TestContext.Current.CancellationToken);
        var state = agent.State;

        AssertImmutable(result.NewMessages);
        AssertImmutable(result.SubscriberErrors);
        AssertImmutable(state.Messages);
        AssertImmutable(state.Tools);
        AssertImmutable(state.PendingToolCallIds);
        AssertImmutable(Assert.IsType<AfterTurnContext>(afterTurn).NewMessages);
        AssertImmutable(afterTurn!.ToolResults);
        AssertImmutable(Assert.IsType<AgentEvent>(ended).Messages);
    }

    private static void AssertImmutable<T>(IReadOnlyCollection<T> values)
    {
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(default!));
    }
}
