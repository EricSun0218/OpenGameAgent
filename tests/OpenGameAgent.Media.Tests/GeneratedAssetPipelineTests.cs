using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using Xunit;

namespace OpenGameAgent.Media.Tests;

public sealed class GeneratedAssetPipelineTests
{
    [Fact]
    public async Task GeneratedAssetToolReportsProgressAndReturnsDurableManifest()
    {
        var input = new GameInput(
            "world",
            "npc",
            "generate-asset",
            "{}",
            new GameMoment("save-1", 42),
            "asset-tool-input");
        var provider = new ToolCallingProvider("generate_asset");
        var options = new AgentOptions(provider, "test-model");
        options.Tools.Add(GameGeneratedAssetTool.Create(
            input,
            "generate_asset",
            "Generate and import an asset.",
            "{\"type\":\"object\",\"additionalProperties\":false}",
            new GameGeneratedAssetPipeline(
                new InMemoryGameGeneratedAssetJobStore(),
                new InMemoryGameGeneratedAssetResourceStore()),
            new ProgressGenerator(Result("image/png", new byte[] { 2, 4, 6 })),
            new TestImporter(),
            (_, _, execution) => new GameGeneratedAssetRequest(
                "asset-tool-" + execution.Call.Id,
                new GameSessionKey(input.SessionId, input.ActorId),
                "portrait",
                input.Moment,
                "test-generator",
                "test-model",
                "test-importer",
                new GameMediaGenerationRequest(
                    "media-" + execution.Call.Id,
                    GameMediaKind.Image,
                    input.PayloadJson))));
        var agent = new Agent(options);
        var progress = new List<ToolProgress>();
        agent.Subscribe((agentEvent, _) =>
        {
            if (agentEvent.Progress is not null)
            {
                progress.Add(agentEvent.Progress);
            }

            return default;
        });

        var result = await agent.RunAsync(
            AgentMessage.UserJson("{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Single(progress);
        Assert.Equal(0.5, progress[0].Fraction);
        var toolMessage = Assert.Single(result.NewMessages, message => message.Role == AgentRole.Tool);
        var json = Assert.IsType<JsonContent>(Assert.Single(toolMessage.Content)).Json;
        Assert.Contains("\"status\":\"Completed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resourceId\":\"sha256-", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncertainGeneratedAssetToolPreventsLaterWritesInTheSameBatch()
    {
        var input = new GameInput(
            "world",
            "npc",
            "generate-asset",
            "{}",
            new GameMoment("save-1", 42),
            "asset-tool-uncertain");
        var laterWriteCalls = 0;
        var options = new AgentOptions(new TwoToolCallingProvider(), "test-model");
        options.Tools.Add(GameGeneratedAssetTool.Create(
            input,
            "generate_asset",
            "Generate and import an asset.",
            "{\"type\":\"object\",\"additionalProperties\":false}",
            new GameGeneratedAssetPipeline(
                new InMemoryGameGeneratedAssetJobStore(),
                new InMemoryGameGeneratedAssetResourceStore()),
            new TestGenerator(new IOException("response lost")),
            new TestImporter(),
            (_, _, execution) => new GameGeneratedAssetRequest(
                "asset-tool-" + execution.Call.Id,
                new GameSessionKey(input.SessionId, input.ActorId),
                "portrait",
                input.Moment,
                "test-generator",
                "test-model",
                "test-importer",
                new GameMediaGenerationRequest(
                    "media-" + execution.Call.Id,
                    GameMediaKind.Image,
                    input.PayloadJson))));
        options.Tools.Add(new AgentTool(
            new ToolDefinition(
                "change_world",
                "Perform another world mutation.",
                "{\"type\":\"object\",\"additionalProperties\":false}"),
            (_, _, _) =>
            {
                Interlocked.Increment(ref laterWriteCalls);
                return new ValueTask<ToolResult>(new ToolResult(Array.Empty<AgentContent>()));
            },
            ToolRisk.NonIdempotentWrite,
            ToolExecutionMode.Sequential));

        var result = await new Agent(options).RunAsync(
            AgentMessage.UserJson("{}"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, laterWriteCalls);
        var tools = result.NewMessages.Where(message => message.Role == AgentRole.Tool).ToArray();
        Assert.Equal(2, tools.Length);
        Assert.Contains(
            "not executed",
            Assert.IsType<TextContent>(Assert.Single(tools[1].Content)).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratesPersistsImportsAndReusesCompletedOperation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var jobs = new InMemoryGameGeneratedAssetJobStore();
        var resources = new InMemoryGameGeneratedAssetResourceStore();
        var pipeline = new GameGeneratedAssetPipeline(jobs, resources);
        var generator = new TestGenerator(Result("image/png", new byte[] { 1, 2, 3, 4 }));
        var importer = new TestImporter();
        var request = Request("asset-1");

        var completed = await pipeline.ExecuteAsync(request, generator, importer, cancellationToken: cancellationToken);
        var repeated = await pipeline.ExecuteAsync(request, generator, importer, cancellationToken: cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.Completed, completed.Status);
        Assert.Equal(completed.Revision, repeated.Revision);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1, importer.ImportCalls);
        Assert.Equal(0, importer.RecoverCalls);
        Assert.NotNull(completed.Manifest);
        Assert.Single(completed.Manifest!.Resources);
        var stored = await resources.ReadAsync(completed.Manifest.Resources[0], cancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, stored.Data);
        Assert.Equal("image/png", stored.MediaType);
    }

    [Fact]
    public async Task ConcurrentExecutionDispatchesGenerationAndImportOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeline = new GameGeneratedAssetPipeline(
            new InMemoryGameGeneratedAssetJobStore(),
            new InMemoryGameGeneratedAssetResourceStore());
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new TestGenerator(
            Result("image/png", new byte[] { 7, 8, 9 }),
            async token => await release.Task.WaitAsync(token));
        var importer = new TestImporter();
        var request = Request("asset-concurrent");

        var first = pipeline.ExecuteAsync(request, generator, importer, cancellationToken: cancellationToken).AsTask();
        await generator.Started.Task.WaitAsync(cancellationToken);
        var second = pipeline.ExecuteAsync(request, generator, importer, cancellationToken: cancellationToken).AsTask();
        release.SetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(GameGeneratedAssetStatus.Completed, result.Status));
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1, importer.ImportCalls);
    }

    [Fact]
    public async Task SeparatePipelineInstancesUseCasToPreventDuplicateGeneration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var jobs = new InMemoryGameGeneratedAssetJobStore();
        var resources = new InMemoryGameGeneratedAssetResourceStore();
        var firstPipeline = new GameGeneratedAssetPipeline(jobs, resources);
        var secondPipeline = new GameGeneratedAssetPipeline(jobs, resources);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new TestGenerator(
            Result("image/png", new byte[] { 5, 6, 7 }),
            async token => await release.Task.WaitAsync(token));
        var importer = new TestImporter();
        var request = Request("asset-cross-pipeline");

        var first = firstPipeline.ExecuteAsync(
            request,
            generator,
            importer,
            cancellationToken: cancellationToken).AsTask();
        await generator.Started.Task.WaitAsync(cancellationToken);
        var second = await secondPipeline.ExecuteAsync(
            request,
            generator,
            importer,
            cancellationToken: cancellationToken);
        release.SetResult(true);
        var completed = await first;

        Assert.Equal(GameGeneratedAssetStatus.Generating, second.Status);
        Assert.Equal(GameGeneratedAssetStatus.Completed, completed.Status);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1, importer.ImportCalls);
    }

    [Fact]
    public async Task GeneratorFailureBecomesUncertainAndNeverBlindlyReplays()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeline = new GameGeneratedAssetPipeline(
            new InMemoryGameGeneratedAssetJobStore(),
            new InMemoryGameGeneratedAssetResourceStore());
        var generator = new TestGenerator(new IOException("connection disappeared"));
        var importer = new TestImporter();
        var request = Request("asset-uncertain");

        var uncertain = await pipeline.ExecuteAsync(request, generator, importer, cancellationToken: cancellationToken);
        var repeated = await pipeline.ExecuteAsync(request, generator, importer, cancellationToken: cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.GenerationUncertain, uncertain.Status);
        Assert.Equal(uncertain.Revision, repeated.Revision);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(0, importer.ImportCalls);
    }

    [Fact]
    public async Task GeneratorAndImporterExceptionsAreNotPersistedVerbatim()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string secret = "secret-prompt-and-key";
        var generationJobs = new InMemoryGameGeneratedAssetJobStore();
        var generationPipeline = new GameGeneratedAssetPipeline(
            generationJobs,
            new InMemoryGameGeneratedAssetResourceStore());
        var request = Request("asset-redaction");

        var generationFailure = await generationPipeline.ExecuteAsync(
            request,
            new TestGenerator(new IOException(secret)),
            new TestImporter(),
            cancellationToken: cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.GenerationUncertain, generationFailure.Status);
        Assert.DoesNotContain(secret, generationFailure.ErrorMessage, StringComparison.Ordinal);

        var importFailure = await new GameGeneratedAssetPipeline(
                new InMemoryGameGeneratedAssetJobStore(),
                new InMemoryGameGeneratedAssetResourceStore())
            .ExecuteAsync(
                Request("asset-import-redaction"),
                new TestGenerator(Result("image/png", new byte[] { 1, 2, 3 })),
                new SecretThrowingImporter(secret),
                cancellationToken: cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.ImportUncertain, importFailure.Status);
        Assert.DoesNotContain(secret, importFailure.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthoritativeRecoveredGenerationResultCompletesWithoutRedispatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var jobs = new InMemoryGameGeneratedAssetJobStore();
        var pipeline = new GameGeneratedAssetPipeline(jobs, new InMemoryGameGeneratedAssetResourceStore());
        var generator = new TestGenerator(new IOException("response lost"));
        var importer = new TestImporter();
        var request = Request("asset-provider-recovery");
        var uncertain = await pipeline.ExecuteAsync(
            request,
            generator,
            importer,
            cancellationToken: cancellationToken);

        var completed = await pipeline.ResolveGenerationAsync(
            request.Owner,
            request.OperationId,
            Result("image/png", new byte[] { 4, 3, 2, 1 }),
            importer,
            cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.GenerationUncertain, uncertain.Status);
        Assert.Equal(GameGeneratedAssetStatus.Completed, completed.Status);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1, importer.ImportCalls);
    }

    [Fact]
    public async Task ImportFailureRecoversWithoutRepeatingWorldMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var jobs = new InMemoryGameGeneratedAssetJobStore();
        var pipeline = new GameGeneratedAssetPipeline(jobs, new InMemoryGameGeneratedAssetResourceStore());
        var generator = new TestGenerator(Result("image/png", new byte[] { 3, 2, 1 }));
        var importer = new TestImporter(throwOnImport: true);
        var request = Request("asset-import-recovery");

        var uncertain = await pipeline.ExecuteAsync(request, generator, importer, cancellationToken: cancellationToken);
        importer.ThrowOnImport = false;
        var completed = await pipeline.ResumeImportAsync(
            request.Owner,
            request.OperationId,
            importer,
            cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.ImportUncertain, uncertain.Status);
        Assert.Equal(GameGeneratedAssetStatus.Completed, completed.Status);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(1, importer.ImportCalls);
        Assert.Equal(1, importer.RecoverCalls);
        Assert.Equal(
            GameGeneratedAssetPipeline.CreateImportOperationId(uncertain),
            completed.ImportReceipt!.OperationId);
    }

    [Fact]
    public async Task DurableActionImporterRecoversExternalEngineCommitWithoutRepeatingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new UncertainThenRecoveredActionHandler();
        var dispatcher = new DurableGameActionDispatcher(new InMemoryGameActionJournal(), handler);
        var importer = new GameGeneratedAssetActionImporter(
            "durable-importer",
            dispatcher,
            context => new GameActionIntent(
                context.ImportOperationId,
                context.Job.OperationId,
                context.Job.Owner.SessionId,
                context.Job.Owner.ActorId,
                "import_generated_asset",
                GameGeneratedAssetActionImporter.CreateManifestArgumentsJson(context),
                context.Job.Moment,
                conflictKey: context.Job.Owner.SessionId + ":generated-assets"));
        var request = new GameGeneratedAssetRequest(
            "asset-external-engine",
            new GameSessionKey("world", "npc"),
            "placeable",
            new GameMoment("save", 12),
            "generator",
            "model",
            importer.ImporterId,
            new GameMediaGenerationRequest(
                "generate-placeable",
                GameMediaKind.Image,
                "{}",
                prompt: "lamp"));
        var pipeline = new GameGeneratedAssetPipeline(
            new InMemoryGameGeneratedAssetJobStore(),
            new InMemoryGameGeneratedAssetResourceStore());

        var uncertain = await pipeline.ExecuteAsync(
            request,
            new TestGenerator(Result("image/png", new byte[] { 1, 3, 5 })),
            importer,
            cancellationToken: cancellationToken);
        var completed = await pipeline.ResumeImportAsync(
            request.Owner,
            request.OperationId,
            importer,
            cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.ImportUncertain, uncertain.Status);
        Assert.Equal(GameGeneratedAssetStatus.Completed, completed.Status);
        Assert.Equal(1, handler.ExecuteCalls);
        Assert.Equal(1, handler.RecoverCalls);
        Assert.Contains("\"resourceId\":\"sha256-", handler.Intent!.ArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", handler.Intent.ArgumentsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationAfterGenerationDispatchIsPersistedAsUncertain()
    {
        var jobs = new InMemoryGameGeneratedAssetJobStore();
        var pipeline = new GameGeneratedAssetPipeline(jobs, new InMemoryGameGeneratedAssetResourceStore());
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new DelegateGenerator(async (_, _, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var request = Request("asset-cancel");
        using var cancellation = new CancellationTokenSource();
        var execution = pipeline.ExecuteAsync(request, generator, new TestImporter(), cancellationToken: cancellation.Token)
            .AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        var persisted = await jobs.LoadAsync(
            request.Owner,
            request.OperationId,
            TestContext.Current.CancellationToken);
        Assert.Equal(GameGeneratedAssetStatus.GenerationUncertain, persisted?.Status);
    }

    [Fact]
    public async Task InvalidOutputFailsClosedBeforeImport()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeline = new GameGeneratedAssetPipeline(
            new InMemoryGameGeneratedAssetJobStore(),
            new InMemoryGameGeneratedAssetResourceStore());
        var importer = new TestImporter();
        var generator = new TestGenerator(new GameMediaGenerationResult(new[]
        {
            new ResourceContent("data:audio/wav;base64,AQID", "audio/wav"),
        }));

        var failed = await pipeline.ExecuteAsync(
            Request("asset-wrong-kind"),
            generator,
            importer,
            cancellationToken: cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.Failed, failed.Status);
        Assert.Equal("invalid_generated_asset", failed.ErrorCode);
        Assert.Equal(0, importer.ImportCalls);
    }

    [Fact]
    public async Task InlineMaterializerRejectsOversizedDataBeforeReturningBytes()
    {
        var materializer = new InlineGameGeneratedAssetMaterializer(maxResourceBytes: 4);
        var resource = new ResourceContent(
            "data:image/png;base64," + Convert.ToBase64String(new byte[7]),
            "image/png");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            materializer.MaterializeAsync(resource, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task InvalidResourceStoreRecordFailsBeforeImport()
    {
        var pipeline = new GameGeneratedAssetPipeline(
            new InMemoryGameGeneratedAssetJobStore(),
            new InvalidResourceStore());
        var importer = new TestImporter();

        var failed = await pipeline.ExecuteAsync(
            Request("asset-invalid-store"),
            new TestGenerator(Result("image/png", new byte[] { 4, 5, 6 })),
            importer,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.Failed, failed.Status);
        Assert.Equal(0, importer.ImportCalls);
    }

    [Fact]
    public async Task HostCanFailAnUnresolvedGenerationWithoutRedispatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeline = new GameGeneratedAssetPipeline(
            new InMemoryGameGeneratedAssetJobStore(),
            new InMemoryGameGeneratedAssetResourceStore());
        var generator = new TestGenerator(new IOException("response lost"));
        var request = Request("asset-provider-not-found");
        await pipeline.ExecuteAsync(request, generator, new TestImporter(), cancellationToken: cancellationToken);

        var failed = await pipeline.FailUnresolvedGenerationAsync(
            request.Owner,
            request.OperationId,
            "provider_job_not_found",
            "The provider confirmed that no result can be recovered.",
            cancellationToken);

        Assert.Equal(GameGeneratedAssetStatus.Failed, failed.Status);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task OperationIdentityCannotBeReboundToDifferentRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipeline = new GameGeneratedAssetPipeline(
            new InMemoryGameGeneratedAssetJobStore(),
            new InMemoryGameGeneratedAssetResourceStore());
        var generator = new TestGenerator(Result("image/png", new byte[] { 1 }));
        var importer = new TestImporter();
        await pipeline.ExecuteAsync(Request("asset-bound"), generator, importer, cancellationToken: cancellationToken);

        var changed = Request("asset-bound", prompt: "different");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(changed, generator, importer, cancellationToken: cancellationToken).AsTask());
    }

    private static GameGeneratedAssetRequest Request(string operationId, string prompt = "paint a tree") => new(
        operationId,
        new GameSessionKey("world", "npc"),
        "character-expression",
        new GameMoment("save-1", 42),
        "test-generator",
        "test-model",
        "test-importer",
        new GameMediaGenerationRequest(
            "request-" + operationId,
            GameMediaKind.Image,
            "{\"weather\":\"rain\"}",
            "{\"size\":\"1024x1024\"}",
            prompt));

    private static GameMediaGenerationResult Result(string mediaType, byte[] bytes) => new(
        new[]
        {
            new ResourceContent(
                "data:" + mediaType + ";base64," + Convert.ToBase64String(bytes),
                mediaType,
                "asset.bin"),
        },
        "{\"seed\":7}",
        "provider-request");

    private sealed class TestGenerator : IGameMediaGenerator
    {
        private readonly GameMediaGenerationResult? _result;
        private readonly Exception? _exception;
        private readonly Func<CancellationToken, Task>? _beforeResult;
        private int _calls;

        public TestGenerator(
            GameMediaGenerationResult result,
            Func<CancellationToken, Task>? beforeResult = null)
        {
            _result = result;
            _beforeResult = beforeResult;
        }

        public TestGenerator(Exception exception)
        {
            _exception = exception;
        }

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult(true);
            if (_beforeResult is not null)
            {
                await _beforeResult(cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return _result!;
        }
    }

    private sealed class ProgressGenerator : IGameMediaGenerator
    {
        private readonly GameMediaGenerationResult _result;

        public ProgressGenerator(GameMediaGenerationResult result)
        {
            _result = result;
        }

        public async ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            if (progress is not null)
            {
                await progress(new GameMediaGenerationProgress("rendering", 0.5), cancellationToken);
            }

            return _result;
        }
    }

    private sealed class ToolCallingProvider : IModelProvider
    {
        private readonly string _toolName;
        private int _calls;

        public ToolCallingProvider(string toolName)
        {
            _toolName = toolName;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                yield return ModelStreamEvent.Terminal(new ModelResponse(
                    new AgentContent[] { new ToolCallContent("asset-call", _toolName, "{}") },
                    ModelStopReason.ToolUse));
                yield break;
            }

            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop));
        }
    }

    private sealed class TwoToolCallingProvider : IModelProvider
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                yield return ModelStreamEvent.Terminal(new ModelResponse(
                    new AgentContent[]
                    {
                        new ToolCallContent("asset-call", "generate_asset", "{}"),
                        new ToolCallContent("write-call", "change_world", "{}"),
                    },
                    ModelStopReason.ToolUse));
                yield break;
            }

            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop));
        }
    }

    private sealed class DelegateGenerator : IGameMediaGenerator
    {
        private readonly Func<GameMediaGenerationRequest, GameMediaProgressHandler?, CancellationToken, ValueTask<GameMediaGenerationResult>> _callback;

        public DelegateGenerator(
            Func<GameMediaGenerationRequest, GameMediaProgressHandler?, CancellationToken, ValueTask<GameMediaGenerationResult>> callback)
        {
            _callback = callback;
        }

        public ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken) => _callback(request, progress, cancellationToken);
    }

    private sealed class TestImporter : IGameGeneratedAssetImporter
    {
        private int _importCalls;
        private int _recoverCalls;

        public TestImporter(bool throwOnImport = false)
        {
            ThrowOnImport = throwOnImport;
        }

        public string ImporterId => "test-importer";

        public bool ThrowOnImport { get; set; }

        public int ImportCalls => Volatile.Read(ref _importCalls);

        public int RecoverCalls => Volatile.Read(ref _recoverCalls);

        public ValueTask<GameGeneratedAssetImportReceipt> ImportAsync(
            GameGeneratedAssetImportContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _importCalls);
            if (ThrowOnImport)
            {
                throw new IOException("connection lost after import dispatch");
            }

            return new ValueTask<GameGeneratedAssetImportReceipt>(Committed(context));
        }

        public ValueTask<GameGeneratedAssetImportReceipt> RecoverAsync(
            GameGeneratedAssetImportContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _recoverCalls);
            return new ValueTask<GameGeneratedAssetImportReceipt>(Committed(context));
        }

        private static GameGeneratedAssetImportReceipt Committed(GameGeneratedAssetImportContext context) =>
            new(
                context.ImportOperationId,
                GameGeneratedAssetImportOutcome.Committed,
                "{\"engineAssetId\":\"asset://tree\"}",
                stateRevision: 11);
    }

    private sealed class SecretThrowingImporter : IGameGeneratedAssetImporter
    {
        private readonly string _secret;

        public SecretThrowingImporter(string secret)
        {
            _secret = secret;
        }

        public string ImporterId => "test-importer";

        public ValueTask<GameGeneratedAssetImportReceipt> ImportAsync(
            GameGeneratedAssetImportContext context,
            CancellationToken cancellationToken) => throw new IOException(_secret);

        public ValueTask<GameGeneratedAssetImportReceipt> RecoverAsync(
            GameGeneratedAssetImportContext context,
            CancellationToken cancellationToken) => throw new IOException(_secret);
    }

    private sealed class InvalidResourceStore : IGameGeneratedAssetResourceStore
    {
        public ValueTask<GameGeneratedAssetResource> SaveAsync(
            string operationId,
            int outputIndex,
            GameGeneratedAssetBinary resource,
            CancellationToken cancellationToken) => new(
                new GameGeneratedAssetResource(
                    "sha256-" + new string('0', 64),
                    new string('0', 64),
                    resource.MediaType,
                    resource.Data.Count));

        public ValueTask<GameGeneratedAssetBinary> ReadAsync(
            GameGeneratedAssetResource resource,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UncertainThenRecoveredActionHandler : IGameActionHandler
    {
        private int _executeCalls;
        private int _recoverCalls;

        public int ExecuteCalls => Volatile.Read(ref _executeCalls);

        public int RecoverCalls => Volatile.Read(ref _recoverCalls);

        public GameActionIntent? Intent { get; private set; }

        public ValueTask<GameActionReceipt> ExecuteAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intent = intent;
            Interlocked.Increment(ref _executeCalls);
            throw new IOException("engine disconnected after committing the asset");
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(
            GameActionIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _recoverCalls);
            return new ValueTask<GameActionReceipt?>(GameActionReceipt.Committed(
                intent,
                "{\"engineAssetId\":\"asset://lamp\"}",
                stateRevision: 19));
        }
    }
}
