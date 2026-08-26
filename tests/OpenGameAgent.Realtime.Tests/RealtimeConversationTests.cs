using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.OpenAI;
using OpenGameAgent.Providers.OpenAI.Realtime;
using Xunit;

namespace OpenGameAgent.Realtime.Tests;

public sealed class RealtimeConversationTests
{
    [Fact]
    public async Task StartingAgainClosesThePriorConversation()
    {
        var first = new FakeTransportSession();
        var second = new FakeTransportSession();
        var transport = new FakeTransport(first, second);
        await using var manager = new RealtimeConversationManager(transport);

        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);

        Assert.True(first.Closed);
        Assert.Equal(RealtimeConversationState.Active, manager.State);
        Assert.Equal(2, transport.ConnectCount);
    }

    [Fact]
    public async Task FullAudioQueueDropsWithoutBlockingTheCaptureThread()
    {
        var session = new FakeTransportSession { BlockAudio = true };
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await manager.StartAsync(
            new RealtimeConversationOptions { AudioQueueCapacity = 1 },
            TestContext.Current.CancellationToken);
        var frame = new RealtimeAudioFrame(new byte[480]);

        Assert.True(manager.TrySendAudio(frame));
        await session.AudioEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(manager.TrySendAudio(frame));
        Assert.False(manager.TrySendAudio(frame));
        Assert.Equal(1, manager.DroppedAudioFrames);

        session.ReleaseAudio.TrySetResult();
    }

    [Fact]
    public async Task HandoffDeliveryDoesNotBlockRealtimeAudioForwarding()
    {
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        var handoffEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = manager.RegisterHandler(async (value, _) =>
        {
            if (value.Kind == RealtimeConversationEventKind.HandoffRequested)
            {
                handoffEntered.TrySetResult();
                await releaseHandoff.Task;
            }
        });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("h1", "build a shelter")));
        await handoffEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(manager.TrySendAudio(new RealtimeAudioFrame(new byte[480])));
        await session.AudioReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        releaseHandoff.TrySetResult();
    }

    [Fact]
    public async Task ClientManagedHandoffsRemainVisibleButAreNotClaimedByTheAutomaticBridge()
    {
        var provider = new CountingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                "{}",
                new GameMoment("world", 1),
                handoff.HandoffId)));
        var observed = new TaskCompletionSource<RealtimeHandoffRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = manager.RegisterHandler((value, _) =>
        {
            if (value.Handoff is not null)
            {
                observed.TrySetResult(value.Handoff);
            }

            return default;
        });
        await manager.StartAsync(
            new RealtimeConversationOptions { ClientManagedHandoffs = true },
            TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("manual", "inspect the area")));

        var handoff = await observed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(handoff.ClientManaged);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, provider.RequestCount);
    }

    [Fact]
    public async Task ClosingFlushesAcceptedInputTranscriptTailExactlyOnce()
    {
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        var tails = new ConcurrentBag<RealtimeHandoffRequest>();
        using var registration = manager.RegisterHandler((value, _) =>
        {
            if (value.Handoff is { IsTranscriptTail: true } handoff)
            {
                tails.Add(handoff);
            }

            return default;
        });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.InputTranscriptDone,
            text: "finish planting the field"));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await manager.StopAsync(TestContext.Current.CancellationToken);

        var tail = Assert.Single(tails);
        Assert.Equal("finish planting the field", tail.Transcript);
        Assert.False(tail.ClientManaged);
    }

    [Fact]
    public async Task AStalledObserverIsBoundedAndRemovedWithoutBlockingOtherObservers()
    {
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalledCalls = 0;
        var healthyCalls = 0;
        var healthyCalledTwice = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var stalled = manager.RegisterHandler(async (_, _) =>
        {
            Interlocked.Increment(ref stalledCalls);
            await release.Task;
        });
        using var healthy = manager.RegisterHandler((_, _) =>
        {
            if (Interlocked.Increment(ref healthyCalls) == 2)
            {
                healthyCalledTwice.TrySetResult();
            }

            return default;
        });
        await manager.StartAsync(
            new RealtimeConversationOptions { EventHandlerTimeoutMilliseconds = 50 },
            TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(RealtimeConversationEventKind.SessionUpdated));
        await WaitUntilAsync(
            () => Volatile.Read(ref healthyCalls) == 1,
            TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(RealtimeConversationEventKind.SessionUpdated));
        await healthyCalledTwice.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref stalledCalls));
        release.TrySetResult();
    }

    [Fact]
    public async Task BargeInCancelsAndTruncatesOnlyPlayedAudio()
    {
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.AudioOutput,
            audio: new RealtimeAudioFrame(new byte[48_000], itemId: "assistant-1")));
        session.Emit(new RealtimeConversationEvent(RealtimeConversationEventKind.InputSpeechStarted));

        var truncated = await session.Truncated.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(("assistant-1", 1_000), truncated);
        Assert.Equal(1, session.CancelCount);
    }

    [Fact]
    public async Task InterruptionDurationAccumulatesSamplesWithoutPerFrameRoundingLoss()
    {
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        for (var index = 0; index < 24; index++)
        {
            session.Emit(new RealtimeConversationEvent(
                RealtimeConversationEventKind.AudioOutput,
                audio: new RealtimeAudioFrame(new byte[2], itemId: "assistant-short")));
        }

        session.Emit(new RealtimeConversationEvent(RealtimeConversationEventKind.InputSpeechStarted));

        var truncated = await session.Truncated.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(("assistant-short", 1), truncated);
    }

    [Fact]
    public async Task ANewBehaviorOnTheSameChannelCancelsThePriorBehavior()
    {
        var session = new FakeTransportSession();
        var behavior = new ReplacingBehaviorHandler();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session), behavior);
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        session.Emit(Behavior("one", "gaze"));
        await behavior.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.Emit(Behavior("two", "gaze"));

        await behavior.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await behavior.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await session.CancelledBehaviorResultReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Contains(session.BehaviorResults, result =>
            result.BehaviorId == "one"
            && result.Disposition == RealtimeBehaviorDisposition.Cancelled);
    }

    [Fact]
    public async Task DistinctBehaviorChannelsAreBounded()
    {
        var session = new FakeTransportSession();
        var behavior = new BlockingBehaviorHandler();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session), behavior);
        await manager.StartAsync(
            new RealtimeConversationOptions { MaximumConcurrentBehaviors = 1 },
            TestContext.Current.CancellationToken);
        session.Emit(Behavior("one", "gaze"));
        await behavior.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        session.Emit(Behavior("two", "gesture"));

        await session.BehaviorResultReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Contains(session.BehaviorResults, result =>
            result.BehaviorId == "two"
            && result.Disposition == RealtimeBehaviorDisposition.Rejected);
        behavior.Release.TrySetResult();
    }

    [Fact]
    public async Task OpenAITransportMapsHandoffAudioAndCredentialsWithoutLeakingTheKey()
    {
        var connection = new FakeWebSocketConnection();
        OpenAIWebSocketConnectRequest? connect = null;
        var transport = new OpenAIRealtimeTransport(new OpenAIRealtimeTransportOptions
        {
            ApiKey = "secret-key",
            ConnectionFactory = (request, _) =>
            {
                connect = request;
                return new ValueTask<IOpenAIWebSocketConnection>(connection);
            },
        });
        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions { Model = "gpt-realtime-test" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(connect);
        Assert.Contains("model=gpt-realtime-test", connect!.Endpoint.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer secret-key", connect.Headers["Authorization"]);
        Assert.DoesNotContain("secret-key", connection.Sent.Single(), StringComparison.Ordinal);

        connection.Receive(JsonSerializer.Serialize(new
        {
            type = "response.function_call_arguments.done",
            name = "handoff",
            call_id = "call-1",
            arguments = "{\"transcript\":\"gather wood\"}",
        }));
        connection.Receive(JsonSerializer.Serialize(new
        {
            type = "response.audio.delta",
            item_id = "audio-1",
            delta = Convert.ToBase64String(new byte[480]),
        }));

        var events = new List<RealtimeConversationEvent>();
        await foreach (var value in session.ReadEventsAsync(TestContext.Current.CancellationToken))
        {
            events.Add(value);
            if (events.Count == 2)
            {
                break;
            }
        }

        Assert.Equal("gather wood", events[0].Handoff!.Transcript);
        Assert.Equal(RealtimeConversationEventKind.AudioOutput, events[1].Kind);
        Assert.Equal(480, events[1].Audio!.Pcm16.Length);
    }

    [Fact]
    public void RemotePlaintextAndTransportControlledHeadersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new OpenAIRealtimeTransport(
            new OpenAIRealtimeTransportOptions { Endpoint = new Uri("ws://example.com/realtime") }));
        Assert.Throws<ArgumentException>(() => new OpenAIRealtimeTransport(
            new OpenAIRealtimeTransportOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = "bad" },
            }));
        Assert.Throws<ArgumentException>(() => new OpenAIRealtimeTransport(
            new OpenAIRealtimeTransportOptions
            {
                Endpoint = new Uri("wss://example.com/v1/realtime"),
                AllowAnonymousLoopback = true,
            }));
    }

    [Fact]
    public async Task ExplicitAnonymousLoopbackRealtimeOmitsAuthorization()
    {
        var connection = new FakeWebSocketConnection();
        OpenAIWebSocketConnectRequest? connect = null;
        var transport = new OpenAIRealtimeTransport(new OpenAIRealtimeTransportOptions
        {
            Endpoint = new Uri("ws://127.0.0.1:8000/v1/realtime"),
            AllowAnonymousLoopback = true,
            ConnectionFactory = (request, _) =>
            {
                connect = request;
                return new ValueTask<IOpenAIWebSocketConnection>(connection);
            },
        });

        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions { Model = "local-voice" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(connect);
        Assert.DoesNotContain("Authorization", connect!.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("model=local-voice", connect.Endpoint.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRealtimeCredentialFailsUnlessLoopbackWasExplicitlyAllowed()
    {
        var transport = new OpenAIRealtimeTransport(new OpenAIRealtimeTransportOptions
        {
            ConnectionFactory = (_, _) => throw new InvalidOperationException("must not connect"),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transport.ConnectAsync(
                new RealtimeConversationOptions(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellingANonCooperativeConnectionDisposesItsLateSocket()
    {
        var completion = new TaskCompletionSource<IOpenAIWebSocketConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeWebSocketConnection();
        var transport = new OpenAIRealtimeTransport(new OpenAIRealtimeTransportOptions
        {
            ApiKey = "secret",
            ConnectionFactory = (_, _) => new ValueTask<IOpenAIWebSocketConnection>(completion.Task),
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var connect = transport.ConnectAsync(new RealtimeConversationOptions(), cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => connect);
        completion.TrySetResult(connection);
        await connection.Disposed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.False(connection.IsOpen);
    }

    [Fact]
    public async Task StartupContextIsBoundedAndSentWithoutCredentials()
    {
        var connection = new FakeWebSocketConnection();
        var transport = new OpenAIRealtimeTransport(new OpenAIRealtimeTransportOptions
        {
            ApiKey = "secret-key",
            ConnectionFactory = (_, _) =>
                new ValueTask<IOpenAIWebSocketConnection>(connection),
        });
        await using var session = await transport.ConnectAsync(new RealtimeConversationOptions
        {
            Instructions = "stay in character",
            StartupContextJson = "{\"weather\":\"rain\"}",
        }, TestContext.Current.CancellationToken);

        var initial = connection.Sent.Single();
        using var document = JsonDocument.Parse(initial);
        var realtimeSession = document.RootElement.GetProperty("session");
        Assert.Contains("startup_context", initial, StringComparison.Ordinal);
        Assert.Contains("weather", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", initial, StringComparison.Ordinal);
        Assert.Equal("realtime", realtimeSession.GetProperty("type").GetString());
        Assert.Equal(
            "audio",
            realtimeSession.GetProperty("output_modalities")[0].GetString());
        Assert.Equal(
            "audio/pcm",
            realtimeSession.GetProperty("audio").GetProperty("input").GetProperty("format")
                .GetProperty("type").GetString());
        Assert.Equal(
            24_000,
            realtimeSession.GetProperty("audio").GetProperty("output").GetProperty("format")
                .GetProperty("rate").GetInt32());
    }

    [Fact]
    public async Task BridgeSteersAnActiveAgentWhileRealtimeAudioContinues()
    {
        var provider = new BlockingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        { });
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                JsonSerializer.Serialize(new { transcript = handoff.Transcript }),
                new GameMoment("world", 10),
                inputId: handoff.HandoffId)));
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("first", "make a plan")));
        await provider.FirstRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("second", "change the plan")));
        Assert.Equal(
            "second",
            await session.HandoffAcknowledged.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        Assert.True(manager.TrySendAudio(new RealtimeAudioFrame(new byte[480])));
        await session.AudioReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        provider.ReleaseFirst.TrySetResult();
        await provider.SecondRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Contains(
            provider.Requests.ElementAt(1).Messages,
            message => message.Content.OfType<TextContent>().Any(content => content.Text == "change the plan"));
    }

    [Fact]
    public async Task DisposingAStaleBridgeDoesNotAbortANewerRunForTheSameActor()
    {
        var provider = new StaleBridgeProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        { });
        var session = new FakeTransportSession { BlockCompletedHandoff = true };
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                "{}",
                new GameMoment("world", 10),
                handoff.HandoffId)));
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("old", "finish the old run")));
        await session.CompletedHandoffEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var newerRun = runtime.RunAsync(new GameInput(
            "session",
            "actor",
            "realtime",
            "{}",
            new GameMoment("world", 11),
            "new"), TestContext.Current.CancellationToken);
        await provider.SecondRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await bridge.DisposeAsync();

        provider.ReleaseSecondRequest.TrySetResult();
        var result = await newerRun.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task BridgeWithoutOwnedRunCoordinatesDoesNotSteerAnExistingRun()
    {
        var provider = new BlockingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        { });
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        long bridgeSequence = 10;
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                "{}",
                new GameMoment("world", Interlocked.Increment(ref bridgeSequence)),
                handoff.HandoffId)));
        var secondObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = manager.RegisterHandler((value, _) =>
        {
            if (string.Equals(value.Handoff?.HandoffId, "second", StringComparison.Ordinal))
            {
                secondObserved.TrySetResult();
            }

            return default;
        });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        var existingRun = runtime.RunAsync(new GameInput(
            "session",
            "actor",
            "realtime",
            "{}",
            new GameMoment("world", 10),
            "existing"), TestContext.Current.CancellationToken);
        await provider.FirstRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("first", "wait for the bridge run")));
        await WaitUntilAsync(() => bridge.HasActiveAgentRun, TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("second", "do not steer the existing run")));
        await secondObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        provider.ReleaseFirst.TrySetResult();
        var result = await existingRun.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AgentResult!.Turns);
    }

    [Fact]
    public async Task LateHandoffFromAStaleBridgeDoesNotSteerANewerRun()
    {
        var provider = new StaleBridgeProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        { });
        var session = new FakeTransportSession { BlockCompletedHandoff = true };
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                "{}",
                new GameMoment("world", 10),
                handoff.HandoffId)));
        var lateObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = manager.RegisterHandler((value, _) =>
        {
            if (string.Equals(value.Handoff?.HandoffId, "late", StringComparison.Ordinal))
            {
                lateObserved.TrySetResult();
            }

            return default;
        });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("old", "finish the old run")));
        await session.CompletedHandoffEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var newerRun = runtime.RunAsync(new GameInput(
            "session",
            "actor",
            "realtime",
            "{}",
            new GameMoment("world", 11),
            "new"), TestContext.Current.CancellationToken);
        await provider.SecondRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("late", "do not steer the new run")));
        await lateObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        provider.ReleaseSecondRequest.TrySetResult();

        var result = await newerRun.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AgentResult!.Turns);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task BridgeDisposalDoesNotAbortRunsForAnotherActorOrSession()
    {
        var provider = new IsolatedBlockingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        { });
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                "{\"id\":\"bridge\"}",
                new GameMoment("world", 10),
                handoff.HandoffId)));
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("bridge", "start")));
        await provider.Started("bridge").WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var otherActor = runtime.RunAsync(new GameInput(
            "session",
            "other-actor",
            "realtime",
            "{\"id\":\"other-actor\"}",
            new GameMoment("world", 10),
            "other-actor"), TestContext.Current.CancellationToken);
        var otherSession = runtime.RunAsync(new GameInput(
            "other-session",
            "actor",
            "realtime",
            "{\"id\":\"other-session\"}",
            new GameMoment("world", 10),
            "other-session"), TestContext.Current.CancellationToken);
        await Task.WhenAll(
            provider.Started("other-actor"),
            provider.Started("other-session")).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await bridge.DisposeAsync();

        provider.Release("other-actor");
        provider.Release("other-session");
        Assert.True((await otherActor).Succeeded);
        Assert.True((await otherSession).Succeeded);
    }

    [Fact]
    public async Task BridgeFlushesAgentDeltasWithoutWaitingForTheRunToFinish()
    {
        var provider = new StreamingBlockingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        { });
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                "{}",
                new GameMoment("world", 10),
                handoff.HandoffId)),
            new GameRealtimeAgentBridgeOptions { HandoffFlushMilliseconds = 50 });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("stream", "describe the plan")));
        var progress = await session.HandoffProgress.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(("stream", "hello"), progress);
        Assert.False(provider.Released);
        provider.Release.TrySetResult();
    }

    [Fact]
    public async Task BridgeHostObserverSeesOrderedRunCoordinatesBeforeToolDispatch()
    {
        var order = new ConcurrentQueue<string>();
        var toolObserverEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToolObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ToolThenTextProvider();
        GameInput? createdInput = null;
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                new AgentTool(
                    new ToolDefinition("inspect", "Inspect state.", "{\"type\":\"object\"}"),
                    (_, _, _) =>
                    {
                        order.Enqueue("tool-executed");
                        toolExecuted.TrySetResult();
                        return new ValueTask<ToolResult>(new ToolResult(
                            new AgentContent[] { new TextContent("ok") }));
                    }),
            }),
        });
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) =>
            {
                createdInput = new GameInput(
                    "session",
                    "actor",
                    "realtime",
                    "{}",
                    new GameMoment("world", 10),
                    handoff.HandoffId);
                return new ValueTask<GameInput>(createdInput);
            },
            new GameRealtimeAgentBridgeOptions
            {
                AgentEventObserver = async (input, agentEvent, _) =>
                {
                    Assert.Same(createdInput, input);
                    order.Enqueue($"{agentEvent.Kind}:{agentEvent.RunId}:{agentEvent.Turn}");
                    if (agentEvent.Kind == AgentEventKind.ToolStarted)
                    {
                        toolObserverEntered.TrySetResult();
                        await releaseToolObserver.Task;
                    }
                },
            });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("observed", "inspect")));
        await toolObserverEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.False(toolExecuted.Task.IsCompleted);
        releaseToolObserver.TrySetResult();
        Assert.Equal(
            "observed",
            await session.HandoffAcknowledged.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        var events = order.ToArray();
        var runStarted = Assert.Single(events, value => value.StartsWith("RunStarted:", StringComparison.Ordinal));
        var runId = runStarted.Split(':')[1];
        Assert.Equal(0, int.Parse(runStarted.Split(':')[2], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains($"TurnStarted:{runId}:1", events);
        Assert.Contains($"TurnStarted:{runId}:2", events);
        Assert.Contains($"RunEnded:{runId}:2", events);
        Assert.True(Array.IndexOf(events, $"ToolStarted:{runId}:1") < Array.IndexOf(events, "tool-executed"));
    }

    [Fact]
    public async Task BridgeHostObserverFailureIsIsolatedFromTheAgentLoop()
    {
        var observed = new ConcurrentQueue<AgentEventKind>();
        var provider = new CountingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                "{}",
                new GameMoment("world", 10),
                handoff.HandoffId)),
            new GameRealtimeAgentBridgeOptions
            {
                AgentEventObserver = (_, agentEvent, _) =>
                {
                    observed.Enqueue(agentEvent.Kind);
                    if (agentEvent.Kind == AgentEventKind.RunStarted)
                    {
                        throw new InvalidOperationException("host observer failed");
                    }

                    return default;
                },
            });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);

        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("failure-isolated", "hello")));
        Assert.Equal(
            "failure-isolated",
            await session.HandoffAcknowledged.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, provider.RequestCount);
        Assert.Contains(AgentEventKind.RunStarted, observed);
        Assert.Contains(AgentEventKind.TurnStarted, observed);
        Assert.Contains(AgentEventKind.RunEnded, observed);
    }

    [Fact]
    public async Task BridgeHostObserverReceivesCancellationDuringShutdown()
    {
        var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new CountingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                "{}",
                new GameMoment("world", 10),
                handoff.HandoffId)),
            new GameRealtimeAgentBridgeOptions
            {
                AgentEventObserver = async (_, agentEvent, cancellationToken) =>
                {
                    if (agentEvent.Kind != AgentEventKind.RunStarted)
                    {
                        return;
                    }

                    observerEntered.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        observerCancelled.TrySetResult();
                        throw;
                    }
                },
            });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("cancel-observer", "hello")));
        await observerEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await bridge.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await observerCancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.False(bridge.HasActiveAgentRun);
    }

    [Fact]
    public async Task BridgeShutdownIsBoundedWhenTheInputFactoryIgnoresCancellation()
    {
        var provider = new CountingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        var session = new FakeTransportSession();
        await using var manager = new RealtimeConversationManager(new FakeTransport(session));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<GameInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (_, _) =>
            {
                entered.TrySetResult();
                return new ValueTask<GameInput>(never.Task);
            },
            new GameRealtimeAgentBridgeOptions { ShutdownTimeoutMilliseconds = 100 });
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);
        session.Emit(new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest("blocked", "inspect")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await bridge.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        never.TrySetCanceled(TestContext.Current.CancellationToken);
    }

    private static RealtimeConversationEvent Behavior(string id, string channel) => new(
        RealtimeConversationEventKind.BehaviorRequested,
        behavior: new RealtimeBehaviorRequest(id, channel, "look", "{}"));

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeTransport : IRealtimeTransport
    {
        private readonly Queue<FakeTransportSession> _sessions;

        public FakeTransport(params FakeTransportSession[] sessions)
        {
            _sessions = new Queue<FakeTransportSession>(sessions);
        }

        public int ConnectCount { get; private set; }

        public ValueTask<IRealtimeTransportSession> ConnectAsync(
            RealtimeConversationOptions options,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            return new ValueTask<IRealtimeTransportSession>(_sessions.Dequeue());
        }
    }

    private sealed class FakeTransportSession : IRealtimeTransportSession
    {
        private readonly BoundedTestQueue<RealtimeConversationEvent> _events = new();

        public bool BlockAudio { get; set; }

        public bool BlockCompletedHandoff { get; set; }

        public bool Closed { get; private set; }

        public int CancelCount { get; private set; }

        public TaskCompletionSource AudioEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseAudio { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AudioReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<(string, int)> Truncated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentBag<RealtimeBehaviorResult> BehaviorResults { get; } = new();

        public TaskCompletionSource BehaviorResultReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancelledBehaviorResultReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<(string, string)> HandoffProgress { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> HandoffAcknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CompletedHandoffEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCompletedHandoff { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(RealtimeConversationEvent value) => _events.Enqueue(value);

        public async IAsyncEnumerable<RealtimeConversationEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                yield return await _events.DequeueAsync(cancellationToken);
            }
        }

        public async ValueTask SendAudioAsync(RealtimeAudioFrame frame, CancellationToken cancellationToken)
        {
            AudioEntered.TrySetResult();
            if (BlockAudio)
            {
                await ReleaseAudio.Task.WaitAsync(cancellationToken);
            }

            AudioReceived.TrySetResult();
        }

        public ValueTask SendTextAsync(string text, RealtimeTextRole role, CancellationToken cancellationToken) => default;

        public async ValueTask SendHandoffAsync(
            string handoffId,
            string text,
            RealtimeHandoffPhase phase,
            bool completed,
            CancellationToken cancellationToken)
        {
            HandoffAcknowledged.TrySetResult(handoffId);
            if (!completed && text.Length > 0)
            {
                HandoffProgress.TrySetResult((handoffId, text));
            }

            if (completed && BlockCompletedHandoff)
            {
                CompletedHandoffEntered.TrySetResult();
                await ReleaseCompletedHandoff.Task.WaitAsync(cancellationToken);
            }
        }

        public ValueTask SendBehaviorResultAsync(RealtimeBehaviorResult result, CancellationToken cancellationToken)
        {
            BehaviorResults.Add(result);
            BehaviorResultReceived.TrySetResult();
            if (result.Disposition == RealtimeBehaviorDisposition.Cancelled)
            {
                CancelledBehaviorResultReceived.TrySetResult();
            }
            return default;
        }

        public ValueTask CancelResponseAsync(CancellationToken cancellationToken)
        {
            CancelCount++;
            return default;
        }

        public ValueTask TruncateAudioAsync(string itemId, int audioEndMilliseconds, CancellationToken cancellationToken)
        {
            Truncated.TrySetResult((itemId, audioEndMilliseconds));
            return default;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            Closed = true;
            _events.Enqueue(new RealtimeConversationEvent(RealtimeConversationEventKind.Closed));
            return default;
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class ReplacingBehaviorHandler : IRealtimeBehaviorHandler
    {
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RealtimeBehaviorResult> ExecuteAsync(
            RealtimeBehaviorRequest request,
            CancellationToken cancellationToken)
        {
            if (request.BehaviorId == "one")
            {
                FirstEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCancelled.TrySetResult();
                    throw;
                }
            }

            SecondEntered.TrySetResult();
            return new RealtimeBehaviorResult(request.BehaviorId, RealtimeBehaviorDisposition.Started);
        }
    }

    private sealed class BlockingBehaviorHandler : IRealtimeBehaviorHandler
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RealtimeBehaviorResult> ExecuteAsync(
            RealtimeBehaviorRequest request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new RealtimeBehaviorResult(
                request.BehaviorId,
                RealtimeBehaviorDisposition.Completed);
        }
    }

    private sealed class BoundedTestQueue<T>
    {
        private readonly ConcurrentQueue<T> _queue = new();
        private readonly SemaphoreSlim _items = new(0);

        public void Enqueue(T value)
        {
            _queue.Enqueue(value);
            _items.Release();
        }

        public async Task<T> DequeueAsync(CancellationToken cancellationToken)
        {
            await _items.WaitAsync(cancellationToken);
            Assert.True(_queue.TryDequeue(out var value));
            return value!;
        }
    }

    private sealed class FakeWebSocketConnection : IOpenAIWebSocketConnection
    {
        private readonly BoundedTestQueue<string> _receive = new();

        public bool IsOpen { get; private set; } = true;

        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Sent { get; } = new();

        public void Receive(string value) => _receive.Enqueue(value);

        public ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
        {
            Sent.Add(text);
            return default;
        }

        public async ValueTask<string> ReceiveTextAsync(int maximumCharacters, CancellationToken cancellationToken) =>
            await _receive.DequeueAsync(cancellationToken);

        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
        {
            IsOpen = false;
            return default;
        }

        public void Dispose()
        {
            IsOpen = false;
            Disposed.TrySetResult();
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        private int _calls;

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondRequestStarted.TrySetResult();
            }

            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
        }
    }

    private sealed class StaleBridgeProvider : IModelProvider
    {
        private int _calls;

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public TaskCompletionSource SecondRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSecondRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            if (Interlocked.Increment(ref _calls) == 2)
            {
                SecondRequestStarted.TrySetResult();
                await ReleaseSecondRequest.Task.WaitAsync(cancellationToken);
            }

            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
        }
    }

    private sealed class IsolatedBlockingProvider : IModelProvider
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _released = new();

        public Task Started(string id) => Signal(_started, id).Task;

        public void Release(string id) => Signal(_released, id).TrySetResult();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var id = ExtractId(request);
            Signal(_started, id).TrySetResult();
            await Signal(_released, id).Task.WaitAsync(cancellationToken);
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
        }

        private static string ExtractId(ModelRequest request)
        {
            foreach (var content in request.Messages
                         .SelectMany(static message => message.Content)
                         .OfType<JsonContent>())
            {
                using var document = JsonDocument.Parse(content.Json);
                if (document.RootElement.TryGetProperty("ActorId", out var actorId)
                    && actorId.GetString() is { } actor)
                {
                    return !string.Equals(request.SessionId, "session", StringComparison.Ordinal)
                        ? "other-session"
                        : string.Equals(actor, "actor", StringComparison.Ordinal)
                            ? "bridge"
                            : "other-actor";
                }
            }

            throw new InvalidOperationException("The test request did not contain an ID.");
        }

        private static TaskCompletionSource Signal(
            ConcurrentDictionary<string, TaskCompletionSource> signals,
            string id) => signals.GetOrAdd(
                id,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class CountingProvider : IModelProvider
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _requestCount);
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
            await Task.CompletedTask;
        }
    }

    private sealed class ToolThenTextProvider : IModelProvider
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(Interlocked.Increment(ref _calls) == 1
                ? new ModelResponse(
                    new AgentContent[] { new ToolCallContent("call-1", "inspect", "{}") },
                    ModelStopReason.ToolUse,
                    new ModelUsage(1, 1))
                : new ModelResponse(
                    new AgentContent[] { new TextContent("done") },
                    ModelStopReason.Stop,
                    new ModelUsage(1, 1)));
        }
    }

    private sealed class StreamingBlockingProvider : IModelProvider
    {
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Released => Release.Task.IsCompleted;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.Started,
                Pending(Array.Empty<AgentContent>()));
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.TextStarted,
                Pending(new AgentContent[] { new TextContent(string.Empty) }),
                contentIndex: 0);
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.TextDelta,
                Pending(new AgentContent[] { new TextContent("hello") }),
                delta: "hello",
                contentIndex: 0);
            await Release.Task.WaitAsync(cancellationToken);
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.TextEnded,
                Pending(new AgentContent[] { new TextContent("hello") }),
                contentIndex: 0,
                content: "hello");
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("hello") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
        }

        private static ModelResponse Pending(IEnumerable<AgentContent> content) =>
            new(content, ModelStopReason.Pending);
    }
}
