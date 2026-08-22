using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using OpenGameAgent.Attachments;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class ImageRuntimeTests
{
    [Fact]
    public async Task InputImagesArePersistedAsReferencesAndResolvedOnlyForTheProvider()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var attachments = new RecordingAttachmentStore();
        var sessions = new InMemoryGameSessionStore();
        var provider = new RecordingProvider(request =>
        {
            var image = Assert.Single(request.Messages.SelectMany(message => message.Content).OfType<BinaryContent>());
            Assert.Equal(Convert.ToBase64String(bytes), image.Data);
            return Text("seen");
        });
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "vision-model")
        {
            ImageAttachments = attachments,
            SessionStore = sessions,
        });
        var input = Input(
            "input-image",
            new BinaryContent(AgentMediaKind.Image, Convert.ToBase64String(bytes), GameImageMediaTypes.Png, "frame.png"));

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, attachments.SaveCount);
        var key = new GameSessionKey(input.SessionId, input.ActorId);
        var snapshot = Assert.IsType<GameSessionSnapshot>(await sessions.LoadAsync(key, TestContext.Current.CancellationToken));
        var reference = Assert.Single(snapshot.Messages.SelectMany(message => message.Content).OfType<ImageAttachmentContent>());
        Assert.Empty(snapshot.Messages.SelectMany(message => message.Content).OfType<BinaryContent>());
        var stored = Assert.IsType<StoredGameImageAttachment>(await runtime.ReadImageAttachmentAsync(
            key,
            reference.Attachment.AttachmentId,
            TestContext.Current.CancellationToken));
        Assert.Equal(bytes, stored.Data.ToArray());
        Assert.Null(await runtime.ReadImageAttachmentAsync(
            new GameSessionKey(input.SessionId, "other-actor"),
            reference.Attachment.AttachmentId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImageBatchIsFullyValidatedBeforeAnyObjectIsSaved()
    {
        var attachments = new RecordingAttachmentStore { FailValidationCall = 2 };
        var provider = new RecordingProvider(_ => Text("must-not-run"));
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "vision-model")
        {
            ImageAttachments = attachments,
        });
        var input = Input(
            "invalid-batch",
            new BinaryContent(AgentMediaKind.Image, "AQ==", GameImageMediaTypes.Png),
            new BinaryContent(AgentMediaKind.Image, "Ag==", GameImageMediaTypes.Png));

        await Assert.ThrowsAsync<GameAttachmentException>(
            () => runtime.RunAsync(input, TestContext.Current.CancellationToken));

        Assert.Equal(2, attachments.ValidateCount);
        Assert.Equal(0, attachments.SaveCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ToolImagesArePersistedBeforeTheNextModelTurnAndSessionCommit()
    {
        var attachments = new RecordingAttachmentStore();
        var sessions = new InMemoryGameSessionStore();
        var provider = new RecordingProvider(request =>
        {
            if (request.Turn == 1)
            {
                return new ModelResponse(
                    new AgentContent[] { new ToolCallContent("call-1", "inspect_scene", "{}") },
                    ModelStopReason.ToolUse);
            }

            var toolImage = Assert.Single(request.Messages
                .Where(message => message.Role == AgentRole.Tool)
                .SelectMany(message => message.Content)
                .OfType<BinaryContent>());
            Assert.Equal("CQgH", toolImage.Data);
            return Text("done");
        });
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "vision-model")
        {
            ImageAttachments = attachments,
            SessionStore = sessions,
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                new AgentTool(
                    new ToolDefinition("inspect_scene", "capture", "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(new AgentContent[]
                    {
                        new BinaryContent(AgentMediaKind.Image, "CQgH", GameImageMediaTypes.Png, "tool.png"),
                    })),
                    ToolRisk.ReadOnly),
            }),
        });
        var input = Input("tool-image");

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, attachments.SaveCount);
        var snapshot = Assert.IsType<GameSessionSnapshot>(await sessions.LoadAsync(
            new GameSessionKey(input.SessionId, input.ActorId),
            TestContext.Current.CancellationToken));
        Assert.Single(snapshot.Messages
            .Where(message => message.Role == AgentRole.Tool)
            .SelectMany(message => message.Content)
            .OfType<ImageAttachmentContent>());
        Assert.Empty(snapshot.Messages.SelectMany(message => message.Content).OfType<BinaryContent>());
    }

    [Fact]
    public async Task ProviderPreflightRejectsBeforeAttachmentBytesAreReadOrProviderIsCalled()
    {
        var attachments = new RecordingAttachmentStore();
        var provider = new RejectingPreflightProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "text-model")
        {
            ImageAttachments = attachments,
        });

        var result = await runtime.RunAsync(
            Input(
                "preflight",
                new BinaryContent(AgentMediaKind.Image, "AQ==", GameImageMediaTypes.Png)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(1, attachments.SaveCount);
        Assert.Equal(0, attachments.ReadCount);
        Assert.Equal(1, provider.PreflightCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task RetryWrapperPreservesPreflightBeforeAttachmentRead()
    {
        var attachments = new RecordingAttachmentStore();
        var inner = new RejectingPreflightProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new RetryingModelProvider(inner),
            "text-model")
        {
            ImageAttachments = attachments,
        });

        var result = await runtime.RunAsync(
            Input(
                "wrapped-preflight",
                new BinaryContent(AgentMediaKind.Image, "AQ==", GameImageMediaTypes.Png)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, attachments.ReadCount);
        Assert.Equal(1, inner.PreflightCount);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public void GameInputRejectsCallerSuppliedDurableImageReferences()
    {
        var attachment = new GameImageAttachment(
            "sha256:" + new string('a', 64),
            GameImageMediaTypes.Png,
            1,
            1,
            1);

        Assert.Throws<ArgumentException>(() => Input(
            "forged-reference",
            new ImageAttachmentContent(attachment)));
    }

    [Fact]
    public async Task RequestProjectorChangesOnlyProviderViewAndPublishesProvenance()
    {
        var attachments = new RecordingAttachmentStore();
        var sessions = new InMemoryGameSessionStore();
        var observer = new ProjectionObserverExtension();
        var provider = new RecordingProvider(request =>
        {
            var user = Assert.Single(request.Messages, message => message.Role == AgentRole.User);
            Assert.Contains(user.Content, content => content is TextContent { Text: "[bounded visual omitted]" });
            var image = Assert.Single(user.Content.OfType<BinaryContent>());
            Assert.Equal("CQgH", image.Data);
            return Text("seen");
        });
        var options = new GameAgentRuntimeOptions(provider, "vision-model")
        {
            ImageAttachments = attachments,
            ImageRequestProjector = new FakeProjector(),
            SessionStore = sessions,
        };
        options.Extensions.Add(observer);
        await using var runtime = new GameAgentRuntime(options);
        var input = Input(
            "projected",
            new BinaryContent(AgentMediaKind.Image, "AQ==", GameImageMediaTypes.Png, "old.png"),
            new BinaryContent(AgentMediaKind.Image, "Ag==", GameImageMediaTypes.Png, "new.png"));

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var snapshot = Assert.IsType<GameSessionSnapshot>(await sessions.LoadAsync(
            new GameSessionKey(input.SessionId, input.ActorId),
            TestContext.Current.CancellationToken));
        Assert.Equal(2, snapshot.Messages.SelectMany(message => message.Content).OfType<ImageAttachmentContent>().Count());
        Assert.Empty(snapshot.Messages.SelectMany(message => message.Content).OfType<BinaryContent>());
        var projected = Assert.Single(observer.Events);
        Assert.Equal(GameImageProjectionDisposition.Replaced, projected.Images[0].Disposition);
        Assert.Equal(GameImageProjectionDisposition.Derived, projected.Images[1].Disposition);
        Assert.NotEqual(projected.Images[1].SourceAttachmentId, projected.Images[1].RequestAttachmentId);
    }

    private static GameInput Input(string inputId, params AgentContent[] content) => new(
        "image-session",
        "image-actor",
        "observe",
        "{}",
        new GameMoment("world", 1),
        inputId,
        content: content);

    private static ModelResponse Text(string text) => new(
        new AgentContent[] { new TextContent(text) },
        ModelStopReason.Stop);

    private sealed class RecordingProvider : IModelProvider
    {
        private readonly Func<ModelRequest, ModelResponse> _handler;
        private int _calls;

        public RecordingProvider(Func<ModelRequest, ModelResponse> handler)
        {
            _handler = handler;
        }

        public int CallCount => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(_handler(request));
        }
    }

    private sealed class RejectingPreflightProvider : IModelProvider, IModelRequestPreflight
    {
        private int _preflightCount;
        private int _callCount;

        public int PreflightCount => Volatile.Read(ref _preflightCount);

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask ValidateRequestAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _preflightCount);
            throw new ModelProviderException("image input is unsupported", isTransient: false);
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(Text("unexpected"));
        }
    }

    private sealed class RecordingAttachmentStore : IGameImageAttachmentStore
    {
        private readonly ConcurrentDictionary<string, StoredGameImageAttachment> _objects = new(StringComparer.Ordinal);
        private int _validateCount;
        private int _saveCount;
        private int _readCount;

        public GameImageAttachmentLimits ImageLimits { get; } = new();

        public int? FailValidationCall { get; set; }

        public int ValidateCount => Volatile.Read(ref _validateCount);

        public int SaveCount => Volatile.Read(ref _saveCount);

        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask ValidateImageAsync(
            SaveGameImageAttachment input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _validateCount);
            if (FailValidationCall == call)
            {
                throw new GameAttachmentException("INVALID_IMAGE", "simulated invalid image");
            }

            return default;
        }

        public ValueTask<GameImageAttachment> SaveImageAsync(
            SaveGameImageAttachment input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _saveCount);
            var data = input.Data.ToArray();
            var id = "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            var attachment = new GameImageAttachment(id, input.MediaType, input.Data.Length, 1, 1, input.Name);
            _objects.TryAdd(id, new StoredGameImageAttachment(attachment, data));
            return new ValueTask<GameImageAttachment>(attachment);
        }

        public ValueTask<StoredGameImageAttachment> ReadImageAsync(
            GameImageAttachment attachment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            if (!_objects.TryGetValue(attachment.AttachmentId, out var stored))
            {
                throw new GameAttachmentException("ATTACHMENT_NOT_FOUND", "missing attachment");
            }

            return new ValueTask<StoredGameImageAttachment>(stored);
        }
    }

    private sealed class FakeProjector : IGameImageRequestProjector
    {
        public ValueTask<GameImageProjectionResult> ProjectAsync(
            GameImageProjectionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameImageProjectionResult>(new GameImageProjectionResult(new[]
            {
                new GameImageProjectionDecision(
                    request.Images[0].Ordinal,
                    request.Images[0].Image.Attachment.AttachmentId,
                    GameImageProjectionDisposition.Replaced,
                    replacementText: "[bounded visual omitted]",
                    transformId: "test-replaced"),
                new GameImageProjectionDecision(
                    request.Images[1].Ordinal,
                    request.Images[1].Image.Attachment.AttachmentId,
                    GameImageProjectionDisposition.Derived,
                    new byte[] { 9, 8, 7 },
                    GameImageMediaTypes.Png,
                    1,
                    1,
                    transformId: "test-derived"),
            }));
        }
    }

    private sealed class ProjectionObserverExtension : IGameAgentExtension
    {
        public List<GameAgentImagesProjectedEvent> Events { get; } = new();

        public GameAgentExtensionDescriptor Descriptor { get; } = new("test.image-projection", "1.0.0");

        public void Configure(GameAgentExtensionApi api)
        {
            api.On(GameAgentExtensionEvents.ImagesProjected, (value, _, _) =>
            {
                Events.Add(value);
                return default;
            });
        }
    }
}
