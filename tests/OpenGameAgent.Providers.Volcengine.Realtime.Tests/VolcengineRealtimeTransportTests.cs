using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenGameAgent.Kernel;
using OpenGameAgent.Realtime;
using Xunit;

namespace OpenGameAgent.Providers.Volcengine.Realtime.Tests;

public sealed class VolcengineRealtimeTransportTests
{
    [Fact]
    public async Task DialogueTranscriptionBecomesHandoffAndAgentTextStreamsThroughTts()
    {
        const string credentialValue = "volc-secret-test-value";
        var dialogue = new FakeConnection();
        var tts = new FakeConnection();
        var requests = new ConcurrentBag<VolcengineWebSocketConnectRequest>();
        var transport = new VolcengineRealtimeTransport(new VolcengineRealtimeTransportOptions
        {
            ApiKey = credentialValue,
            ConnectionFactory = (request, _) =>
            {
                requests.Add(request);
                return new ValueTask<IVolcengineWebSocketConnection>(
                    request.Endpoint.AbsolutePath.Contains("/dialogue", StringComparison.Ordinal)
                        ? dialogue
                        : tts);
            },
        });

        Assert.Equal(
            RealtimeTransportFeatures.AudioInput
            | RealtimeTransportFeatures.InputTranscription
            | RealtimeTransportFeatures.AudioOutput
            | RealtimeTransportFeatures.OutputTranscription
            | RealtimeTransportFeatures.SpeechBoundaries
            | RealtimeTransportFeatures.ResponseCancellation
            | RealtimeTransportFeatures.Handoff,
            transport.Features);

        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions(),
            TestContext.Current.CancellationToken);
        await using var events = session.ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        await session.SendAudioAsync(
            new RealtimeAudioFrame(new byte[640], 16_000),
            TestContext.Current.CancellationToken);
        dialogue.Emit(ServerEvent(VolcengineEvents.AsrInfo, "dialogue", "{}"));
        dialogue.Emit(ServerEvent(
            VolcengineEvents.AsrResponse,
            "dialogue",
            "{\"result\":{\"text\":\"build a shelter\"}}"));
        dialogue.Emit(ServerEvent(VolcengineEvents.AsrEnded, "dialogue", "{}"));

        var observed = await ReadThroughAsync(
            events,
            RealtimeConversationEventKind.HandoffRequested,
            TestContext.Current.CancellationToken);
        Assert.Contains(observed, value => value.Kind == RealtimeConversationEventKind.InputSpeechStarted);
        Assert.Contains(observed, value => value.Kind == RealtimeConversationEventKind.InputSpeechStopped);
        Assert.Contains(observed, value => value.Kind == RealtimeConversationEventKind.InputTranscriptDone);
        var handoff = Assert.Single(observed, value => value.Handoff is not null).Handoff!;
        Assert.Equal("build a shelter", handoff.Transcript);

        await session.SendHandoffAsync(
            handoff.HandoffId,
            "I will do that.",
            RealtimeHandoffPhase.Final,
            completed: true,
            TestContext.Current.CancellationToken);
        var ttsSession = tts.Sent
            .Select(ParseClient)
            .Last(value => value.Event == VolcengineEvents.StartSession)
            .SessionId!;
        var taskRequest = tts.Sent
            .Select(ParseClient)
            .Single(value => value.Event == VolcengineEvents.TaskRequest);
        using (var document = JsonDocument.Parse(taskRequest.Payload))
        {
            Assert.Equal(
                "I will do that.",
                document.RootElement.GetProperty("req_params").GetProperty("text").GetString());
        }
        tts.Emit(ServerEvent(VolcengineEvents.TtsSentenceStart, ttsSession, "{}"));
        tts.Emit(ServerEvent(
            VolcengineEvents.TtsResponse,
            ttsSession,
            new byte[480],
            VolcengineMessageType.AudioOnlyServer,
            VolcengineSerialization.Raw));
        tts.Emit(ServerEvent(
            VolcengineEvents.TtsSubtitle,
            ttsSession,
            "{\"words\":[{\"word\":\"好\",\"startTime\":0.1,\"endTime\":0.25,\"confidence\":0.9}]}"));
        tts.Emit(ServerEvent(VolcengineEvents.TtsEnded, ttsSession, "{}"));

        var output = await ReadThroughAsync(
            events,
            RealtimeConversationEventKind.ResponseDone,
            TestContext.Current.CancellationToken);
        var audio = Assert.Single(output, value => value.Kind == RealtimeConversationEventKind.AudioOutput);
        Assert.Equal(24_000, audio.Audio!.SampleRate);
        var subtitle = Assert.Single(output, value => value.Timing is not null);
        Assert.Equal("好", subtitle.Text);
        Assert.Equal(100, subtitle.Timing!.StartMilliseconds);
        Assert.Equal(250, subtitle.Timing.EndMilliseconds);
        Assert.True(subtitle.Timing.WordLevel);

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal(credentialValue, request.Headers["X-Api-Key"]));
        Assert.DoesNotContain(
            tts.Sent.Concat(dialogue.Sent),
            payload => Encoding.UTF8.GetString(payload).Contains(credentialValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentSessionsSnapshotDifferentConversationVoices()
    {
        var firstConnection = new FakeConnection();
        var secondConnection = new FakeConnection();
        var connections = new ConcurrentQueue<FakeConnection>(new[]
        {
            firstConnection,
            secondConnection,
        });
        var transport = new VolcengineRealtimeTransport(new VolcengineRealtimeTransportOptions
        {
            InputMode = VolcengineRealtimeInputMode.Disabled,
            ApiKey = "test-credential",
            Speaker = "fallback-speaker",
            ConnectionFactory = (_, _) => new ValueTask<IVolcengineWebSocketConnection>(
                connections.TryDequeue(out var connection)
                    ? connection
                    : throw new InvalidOperationException("Unexpected connection.")),
        });

        var firstTask = transport.ConnectAsync(
            new RealtimeConversationOptions { Voice = "npc-speaker-a" },
            TestContext.Current.CancellationToken).AsTask();
        var secondTask = transport.ConnectAsync(
            new RealtimeConversationOptions { Voice = "npc-speaker-b" },
            TestContext.Current.CancellationToken).AsTask();
        await Task.WhenAll(firstTask, secondTask);
        await using var first = await firstTask;
        await using var second = await secondTask;

        await Task.WhenAll(
            first.SendHandoffAsync(
                "handoff-a",
                "first voice",
                RealtimeHandoffPhase.Final,
                completed: true,
                TestContext.Current.CancellationToken).AsTask(),
            second.SendHandoffAsync(
                "handoff-b",
                "second voice",
                RealtimeHandoffPhase.Final,
                completed: true,
                TestContext.Current.CancellationToken).AsTask());

        var speakers = new[] { firstConnection, secondConnection }
            .Select(connection => connection.Sent
                .Select(ParseClient)
                .Where(frame => frame.Event == VolcengineEvents.StartSession)
                .Select(frame =>
                {
                    using var payload = JsonDocument.Parse(frame.Payload);
                    return payload.RootElement
                        .GetProperty("req_params")
                        .GetProperty("speaker")
                        .GetString();
                })
                .Single())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "npc-speaker-a", "npc-speaker-b" }, speakers);
    }

    [Fact]
    public async Task PlaceholderVoiceUsesTransportFallbackAndInvalidVoiceFailsBeforeDispatch()
    {
        var connection = new FakeConnection();
        var calls = 0;
        var transport = new VolcengineRealtimeTransport(new VolcengineRealtimeTransportOptions
        {
            InputMode = VolcengineRealtimeInputMode.Disabled,
            ApiKey = "test-credential",
            Speaker = "configured-fallback",
            ConnectionFactory = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return new ValueTask<IVolcengineWebSocketConnection>(connection);
            },
        });

        await using (var session = await transport.ConnectAsync(
            new RealtimeConversationOptions { Voice = "alloy" },
            TestContext.Current.CancellationToken))
        {
            await session.SendHandoffAsync(
                "fallback-handoff",
                "fallback voice",
                RealtimeHandoffPhase.Final,
                completed: true,
                TestContext.Current.CancellationToken);
        }

        var start = connection.Sent
            .Select(ParseClient)
            .Single(frame => frame.Event == VolcengineEvents.StartSession);
        using (var payload = JsonDocument.Parse(start.Payload))
        {
            Assert.Equal(
                "configured-fallback",
                payload.RootElement.GetProperty("req_params").GetProperty("speaker").GetString());
        }

        var beforeInvalid = calls;
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ConnectAsync(
            new RealtimeConversationOptions { Voice = new string('x', 257) },
            TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(beforeInvalid, calls);
    }

    [Fact]
    public async Task BargeInCancelsTheTtsSubSessionAndDropsLateAudio()
    {
        var dialogue = new FakeConnection();
        var tts = new FakeConnection();
        var transport = CreateFakeTransport(dialogue, tts, "barge-secret");
        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions(),
            TestContext.Current.CancellationToken);
        await using var events = session.ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        await session.SendHandoffAsync(
            "response-1",
            "old speech",
            RealtimeHandoffPhase.Commentary,
            completed: false,
            TestContext.Current.CancellationToken);
        var ttsSession = tts.Sent
            .Select(ParseClient)
            .Last(value => value.Event == VolcengineEvents.StartSession)
            .SessionId!;
        await session.CancelResponseAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            tts.Sent.Select(ParseClient),
            value => value.Event == VolcengineEvents.CancelSession && value.SessionId == ttsSession);
        var cancelled = await ReadThroughAsync(
            events,
            RealtimeConversationEventKind.ResponseCancelled,
            TestContext.Current.CancellationToken);
        Assert.Equal("response-1", cancelled.Last().ResponseId);

        tts.Emit(ServerEvent(
            VolcengineEvents.TtsResponse,
            ttsSession,
            new byte[480],
            VolcengineMessageType.AudioOnlyServer,
            VolcengineSerialization.Raw));
        tts.Emit(ServerEvent(VolcengineEvents.SessionCanceled, ttsSession, "{}"));

        await session.SendHandoffAsync(
            "response-2",
            "new speech",
            RealtimeHandoffPhase.Final,
            completed: true,
            TestContext.Current.CancellationToken);
        var nextTtsSession = tts.Sent
            .Select(ParseClient)
            .Last(value => value.Event == VolcengineEvents.StartSession)
            .SessionId!;
        var expectedAudio = Enumerable.Repeat((byte)7, 480).ToArray();
        tts.Emit(ServerEvent(
            VolcengineEvents.TtsResponse,
            nextTtsSession,
            expectedAudio,
            VolcengineMessageType.AudioOnlyServer,
            VolcengineSerialization.Raw));
        var next = await ReadThroughAsync(
            events,
            RealtimeConversationEventKind.AudioOutput,
            TestContext.Current.CancellationToken);
        var audio = Assert.Single(next, value => value.Kind == RealtimeConversationEventKind.AudioOutput);
        Assert.Equal(expectedAudio, audio.Audio!.Pcm16.ToArray());
    }

    [Fact]
    public async Task BridgeBargeInCancelsSpeechWithoutRepeatingCommittedGameAction()
    {
        var dialogue = new FakeConnection();
        var tts = new FakeConnection();
        var transport = CreateFakeTransport(dialogue, tts, "bridge-secret");
        var provider = new ToolThenTextProvider();
        var journal = new InMemoryGameActionJournal();
        var handler = new RecordingActionHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            ToolProvider = (input, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                GameActionTool.Create(
                    input,
                    "set_world_flag",
                    "Commit one authoritative game mutation.",
                    "{\"type\":\"object\",\"required\":[\"enabled\"],\"properties\":{\"enabled\":{\"type\":\"boolean\"}},\"additionalProperties\":false}",
                    dispatcher),
            }),
        });
        await using var manager = new RealtimeConversationManager(transport);
        var audioObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var audioRegistration = manager.RegisterHandler((value, _) =>
        {
            if (value.Kind == RealtimeConversationEventKind.AudioOutput)
            {
                audioObserved.TrySetResult();
            }

            return default;
        });
        await using var bridge = new GameRealtimeAgentBridge(
            runtime,
            manager,
            new GameSessionKey("session", "actor"),
            (handoff, _) => new ValueTask<GameInput>(new GameInput(
                "session",
                "actor",
                "realtime",
                JsonSerializer.Serialize(new { transcript = handoff.Transcript }),
                new GameMoment("world", 42),
                inputId: handoff.HandoffId)));
        await manager.StartAsync(new RealtimeConversationOptions(), TestContext.Current.CancellationToken);

        dialogue.Emit(ServerEvent(VolcengineEvents.AsrInfo, "dialogue", "{}"));
        dialogue.Emit(ServerEvent(
            VolcengineEvents.AsrResponse,
            "dialogue",
            "{\"result\":{\"text\":\"change the world\"}}"));
        dialogue.Emit(ServerEvent(VolcengineEvents.AsrEnded, "dialogue", "{}"));

        var committedIntent = await handler.Committed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => tts.Sent.Select(ParseClient).Any(value => value.Event == VolcengineEvents.TaskRequest),
            TestContext.Current.CancellationToken);
        var ttsSession = tts.Sent
            .Select(ParseClient)
            .Last(value => value.Event == VolcengineEvents.StartSession)
            .SessionId!;
        tts.Emit(ServerEvent(
            VolcengineEvents.TtsResponse,
            ttsSession,
            Enumerable.Repeat((byte)3, 480).ToArray(),
            VolcengineMessageType.AudioOnlyServer,
            VolcengineSerialization.Raw));
        await audioObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        dialogue.Emit(ServerEvent(VolcengineEvents.AsrInfo, "dialogue", "{}"));
        await WaitUntilAsync(
            () => tts.Sent.Select(ParseClient).Any(value =>
                value.Event == VolcengineEvents.CancelSession && value.SessionId == ttsSession),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.ExecuteCount);
        var persisted = await journal.FindAsync(
            committedIntent.OperationId,
            TestContext.Current.CancellationToken);
        Assert.Equal(GameActionStatus.Committed, persisted!.Receipt!.Status);
    }

    [Fact]
    public async Task ManagerStopAfterCompletedTtsIsIdempotent()
    {
        var tts = new FakeConnection();
        var transport = CreateTtsOnlyTransport(tts, "lifecycle-credential");
        var audio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RealtimeConversationManager(transport);
        using var handler = manager.RegisterHandler((value, _) =>
        {
            if (value.Kind == RealtimeConversationEventKind.AudioOutput)
            {
                audio.TrySetResult();
            }

            if (value.Kind == RealtimeConversationEventKind.ResponseDone)
            {
                done.TrySetResult();
            }

            return default;
        });
        await manager.StartAsync(
            new RealtimeConversationOptions(),
            TestContext.Current.CancellationToken);
        await manager.SendTextAsync(
            "lifecycle smoke",
            RealtimeTextRole.Assistant,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => tts.Sent.Select(ParseClient).Any(value =>
                value.Event == VolcengineEvents.StartSession),
            TestContext.Current.CancellationToken);
        var sessionId = tts.Sent
            .Select(ParseClient)
            .Last(value => value.Event == VolcengineEvents.StartSession)
            .SessionId!;
        tts.Emit(ServerEvent(
            VolcengineEvents.TtsResponse,
            sessionId,
            new byte[480],
            VolcengineMessageType.AudioOnlyServer,
            VolcengineSerialization.Raw));
        tts.Emit(ServerEvent(VolcengineEvents.TtsEnded, sessionId, "{}"));
        await Task.WhenAll(audio.Task, done.Task).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await manager.StopAsync(TestContext.Current.CancellationToken);
        await manager.StopAsync(TestContext.Current.CancellationToken);
        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.Equal(1, tts.DisposeCount);
        Assert.Equal(RealtimeConversationState.Closed, manager.State);
    }

    [Fact]
    public async Task AwaitUsingDisposesAnActiveSessionExactlyOnce()
    {
        var tts = new FakeConnection();
        var transport = CreateTtsOnlyTransport(tts, "scope-credential");

        await using (var manager = new RealtimeConversationManager(transport))
        {
            await manager.StartAsync(
                new RealtimeConversationOptions(),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, tts.DisposeCount);
    }

    [Fact]
    public async Task RemoteCloseRacesWithStopAndDisposeWithoutDoubleDisposal()
    {
        var tts = new FakeConnection();
        var transport = CreateTtsOnlyTransport(tts, "remote-close-credential");
        var manager = new RealtimeConversationManager(transport);
        await manager.StartAsync(
            new RealtimeConversationOptions(),
            TestContext.Current.CancellationToken);

        tts.Emit(ServerEvent(VolcengineEvents.ConnectionFinished, "connect", "{}"));
        await WaitUntilAsync(
            () => manager.State == RealtimeConversationState.Closed,
            TestContext.Current.CancellationToken);
        await Task.WhenAll(
            manager.StopAsync(TestContext.Current.CancellationToken).AsTask(),
            manager.DisposeAsync().AsTask());

        Assert.Equal(1, tts.DisposeCount);
        Assert.Equal(RealtimeConversationState.Closed, manager.State);
    }

    [Fact]
    public async Task ProviderErrorsCannotEchoCredentialsOrHeaderSecrets()
    {
        const string credentialValue = "super-secret-api-value";
        const string privateHeaderValue = "private-routing-token";
        var tts = new FakeConnection();
        var transport = new VolcengineRealtimeTransport(new VolcengineRealtimeTransportOptions
        {
            InputMode = VolcengineRealtimeInputMode.Disabled,
            ApiKey = credentialValue,
            Headers = new Dictionary<string, string> { ["X-Game-Routing"] = privateHeaderValue },
            ConnectionFactory = (_, _) => new ValueTask<IVolcengineWebSocketConnection>(tts),
        });
        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions(),
            TestContext.Current.CancellationToken);
        await using var events = session.ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        tts.Emit(ServerError(401, $"{{\"message\":\"{credentialValue} {privateHeaderValue}\"}}"));
        var observed = await ReadThroughAsync(
            events,
            RealtimeConversationEventKind.Error,
            TestContext.Current.CancellationToken);
        var error = observed.Last().Error!;
        Assert.DoesNotContain(credentialValue, error, StringComparison.Ordinal);
        Assert.DoesNotContain(privateHeaderValue, error, StringComparison.Ordinal);
        Assert.Contains("[redacted]", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupFailureDoesNotRetainASecretBearingInnerException()
    {
        const string credentialValue = "volc-secret-that-must-not-escape";
        var transport = new VolcengineRealtimeTransport(new VolcengineRealtimeTransportOptions
        {
            ApiKey = credentialValue,
            ConnectionFactory = (request, _) => throw new InvalidOperationException(
                $"upstream rejected {request.Headers["X-Api-Key"]}"),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transport.ConnectAsync(new RealtimeConversationOptions(), CancellationToken.None));

        Assert.DoesNotContain(credentialValue, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task NonCooperativeCredentialResolutionCanBeCancelledWithoutDispatch()
    {
        var never = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var transport = new VolcengineRealtimeTransport(new VolcengineRealtimeTransportOptions
        {
            GetApiKeyAsync = _ => new ValueTask<string?>(never.Task),
            ConnectionFactory = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return new ValueTask<IVolcengineWebSocketConnection>(new FakeConnection());
            },
        });
        using var cancellation = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.ConnectAsync(new RealtimeConversationOptions(), cancellation.Token).AsTask());
        Assert.Equal(0, calls);
        never.TrySetResult("late-secret");
    }

    [Fact]
    public void RemotePlaintextAndControlledHeadersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new VolcengineRealtimeTransport(
            new VolcengineRealtimeTransportOptions
            {
                TtsEndpoint = new Uri("ws://example.com/tts"),
            }));
        Assert.Throws<ArgumentException>(() => new VolcengineRealtimeTransport(
            new VolcengineRealtimeTransportOptions
            {
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "not-allowed" },
            }));
    }

    [Fact]
    public void WireDecoderRejectsTruncationAndBoundedGzipExpansion()
    {
        Assert.Throws<InvalidDataException>(() =>
            VolcengineWireProtocol.Decode(new byte[] { 0x11, 0x94, 0x10 }, 1024));

        var compressed = ServerEvent(
            VolcengineEvents.AsrResponse,
            "session",
            new string('x', 4096),
            compression: VolcengineCompression.Gzip);
        Assert.Throws<InvalidDataException>(() => VolcengineWireProtocol.Decode(compressed, 512));
    }

    [Fact]
    public async Task LiveTtsSmokeRunsOnlyWhenApiKeyIsConfigured()
    {
        var apiKey = Environment.GetEnvironmentVariable("VOLCENGINE_TTS_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        var transport = new VolcengineRealtimeTransport(new VolcengineRealtimeTransportOptions
        {
            InputMode = VolcengineRealtimeInputMode.Disabled,
            ApiKey = apiKey,
            Speaker = Environment.GetEnvironmentVariable("VOLCENGINE_TTS_VOICE")
                ?? "zh_female_gaolengyujie_uranus_bigtts",
            ConnectTimeoutMilliseconds = 30_000,
            WireOperationTimeoutMilliseconds = 30_000,
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = await transport.ConnectAsync(
            new RealtimeConversationOptions(),
            timeout.Token);
        await using var events = session.ReadEventsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        await session.SendHandoffAsync(
            "live-smoke",
            "你好。",
            RealtimeHandoffPhase.Final,
            completed: true,
            timeout.Token);
        var observed = await ReadThroughAsync(
            events,
            RealtimeConversationEventKind.ResponseDone,
            timeout.Token);
        Assert.DoesNotContain(
            observed,
            value => (value.Error ?? string.Empty).Contains(apiKey, StringComparison.Ordinal));
        Assert.True(
            observed.Any(value => value.Kind == RealtimeConversationEventKind.AudioOutput),
            string.Join(" | ", observed.Select(value => $"{value.Kind}:{value.Error}")));
    }

    private static VolcengineRealtimeTransport CreateFakeTransport(
        FakeConnection dialogue,
        FakeConnection tts,
        string secret) => new(new VolcengineRealtimeTransportOptions
        {
            ApiKey = secret,
            ConnectionFactory = (request, _) =>
                new ValueTask<IVolcengineWebSocketConnection>(
                    request.Endpoint.AbsolutePath.Contains("/dialogue", StringComparison.Ordinal)
                        ? dialogue
                        : tts),
        });

    private static VolcengineRealtimeTransport CreateTtsOnlyTransport(
        FakeConnection tts,
        string credentialValue) => new(new VolcengineRealtimeTransportOptions
        {
            InputMode = VolcengineRealtimeInputMode.Disabled,
            ApiKey = credentialValue,
            ConnectionFactory = (_, _) =>
                new ValueTask<IVolcengineWebSocketConnection>(tts),
        });

    private static async Task<List<RealtimeConversationEvent>> ReadThroughAsync(
        IAsyncEnumerator<RealtimeConversationEvent> events,
        RealtimeConversationEventKind terminal,
        CancellationToken cancellationToken)
    {
        var values = new List<RealtimeConversationEvent>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (await events.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
        {
            values.Add(events.Current);
            if (events.Current.Kind == terminal)
            {
                return values;
            }
        }

        throw new InvalidOperationException($"Realtime event {terminal} was not observed.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static byte[] ServerError(int code, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var result = new byte[12 + bytes.Length];
        result[0] = 0x11;
        result[1] = 0xf0;
        result[2] = 0x10;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4, 4), code);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(8, 4), bytes.Length);
        bytes.CopyTo(result, 12);
        return result;
    }

    private static byte[] ServerEvent(
        int eventType,
        string sessionId,
        string json,
        VolcengineMessageType type = VolcengineMessageType.FullServerResponse,
        VolcengineSerialization serialization = VolcengineSerialization.Json,
        VolcengineCompression compression = VolcengineCompression.None) =>
        ServerEvent(eventType, sessionId, Encoding.UTF8.GetBytes(json), type, serialization, compression);

    private static byte[] ServerEvent(
        int eventType,
        string sessionId,
        byte[] payload,
        VolcengineMessageType type = VolcengineMessageType.FullServerResponse,
        VolcengineSerialization serialization = VolcengineSerialization.Json,
        VolcengineCompression compression = VolcengineCompression.None)
    {
        var encoded = compression == VolcengineCompression.Gzip ? Compress(payload) : payload;
        var connectionEvent = VolcengineEvents.HasNoSessionId(eventType);
        var id = Encoding.UTF8.GetBytes(connectionEvent ? "connect" : sessionId);
        var result = new byte[4 + 4 + 4 + id.Length + 4 + encoded.Length];
        result[0] = 0x11;
        result[1] = (byte)(((byte)type << 4) | 4);
        result[2] = (byte)(((byte)serialization << 4) | (byte)compression);
        var offset = 4;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset, 4), eventType);
        offset += 4;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset, 4), id.Length);
        offset += 4;
        id.CopyTo(result, offset);
        offset += id.Length;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset, 4), encoded.Length);
        offset += 4;
        encoded.CopyTo(result, offset);
        return result;
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(source);
        }

        return output.ToArray();
    }

    private static ClientFrame ParseClient(byte[] frame)
    {
        var type = (VolcengineMessageType)(frame[1] >> 4);
        var compression = (VolcengineCompression)(frame[2] & 0x0f);
        var offset = (frame[0] & 0x0f) * 4;
        var eventType = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(offset, 4));
        offset += 4;
        string? sessionId = null;
        if (!VolcengineEvents.HasNoSessionId(eventType))
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(offset, 4));
            offset += 4;
            sessionId = Encoding.UTF8.GetString(frame, offset, length);
            offset += length;
        }

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(offset, 4));
        offset += 4;
        var payload = frame.AsSpan(offset, payloadLength).ToArray();
        if (compression == VolcengineCompression.Gzip)
        {
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            payload = output.ToArray();
        }

        return new ClientFrame(type, eventType, sessionId, payload);
    }

    private sealed record ClientFrame(
        VolcengineMessageType Type,
        int Event,
        string? SessionId,
        byte[] Payload);

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
            var toolTurn = Interlocked.Increment(ref _calls) == 1;
            var content = toolTurn
                ? new AgentContent[]
                {
                    new ToolCallContent("tool-1", "set_world_flag", "{\"enabled\":true}"),
                }
                : new AgentContent[] { new TextContent("The world change is committed.") };
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                content,
                toolTurn ? ModelStopReason.ToolUse : ModelStopReason.Stop,
                new ModelUsage(1, 1)));
        }
    }

    private sealed class RecordingActionHandler : IGameActionHandler
    {
        private int _executeCount;

        public int ExecuteCount => Volatile.Read(ref _executeCount);

        public TaskCompletionSource<GameActionIntent> Committed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<GameActionReceipt> ExecuteAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executeCount);
            Committed.TrySetResult(intent);
            return new ValueTask<GameActionReceipt>(
                GameActionReceipt.Committed(intent, "{\"ok\":true}"));
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken)
        {
            _ = intent;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameActionReceipt?>((GameActionReceipt?)null);
        }
    }

    private sealed class FakeConnection : IVolcengineWebSocketConnection
    {
        private readonly Channel<byte[]> _receive = Channel.CreateUnbounded<byte[]>();
        private readonly ConcurrentQueue<byte[]> _sent = new();
        private int _disposed;
        private int _disposeCount;

        public bool IsOpen => Volatile.Read(ref _disposed) == 0;

        public IReadOnlyList<byte[]> Sent => _sent.ToArray();

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask SendBinaryAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = payload.ToArray();
            _sent.Enqueue(copy);
            var request = ParseClient(copy);
            if (request.Event == VolcengineEvents.StartConnection)
            {
                Emit(ServerEvent(VolcengineEvents.ConnectionStarted, "connect", "{}"));
            }
            else if (request.Event == VolcengineEvents.StartSession)
            {
                Emit(ServerEvent(VolcengineEvents.SessionStarted, request.SessionId!, "{}"));
            }

            return default;
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReceiveBinaryAsync(
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            var value = await _receive.Reader.ReadAsync(cancellationToken);
            if (value.Length > maximumBytes)
            {
                throw new InvalidDataException("fake frame too large");
            }

            return value;
        }

        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
        {
            _ = reason;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public void Emit(byte[] value) => _receive.Writer.TryWrite(value);

        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeCount) != 1)
            {
                return;
            }

            Interlocked.Exchange(ref _disposed, 1);
            _receive.Writer.TryComplete();
        }
    }
}
