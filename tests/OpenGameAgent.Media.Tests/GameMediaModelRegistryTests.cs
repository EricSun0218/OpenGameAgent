using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using Xunit;

namespace OpenGameAgent.Media.Tests;

public sealed class GameMediaModelRegistryTests
{
    [Fact]
    public void RegistersListsReplacesAndRemovesMediaProviders()
    {
        using var registry = new GameMediaModelRegistry(new GameMediaModelRegistryOptions { MaxProviders = 2 });
        registry.Register(Registration("visual", new[]
        {
            Model("visual", "image", GameMediaKind.Image),
            Model("visual", "video", GameMediaKind.Video),
        }));
        registry.Register(Registration("voice", new[] { Model("voice", "speech", GameMediaKind.Audio) }));

        Assert.Equal(new[] { "visual", "voice" }, registry.GetProviders().Select(item => item.Descriptor.ProviderId));
        Assert.Equal(3, registry.GetModels().Count);
        Assert.Equal("speech", registry.GetModel("voice", "speech")?.ModelId);

        registry.Register(
            Registration("visual", new[] { Model("visual", "new-image", GameMediaKind.Image) }),
            replace: true);
        Assert.Null(registry.GetModel("visual", "image"));
        Assert.NotNull(registry.GetModel("visual", "new-image"));
        Assert.True(registry.Unregister("voice"));
        Assert.False(registry.Unregister("voice"));
    }

    [Fact]
    public async Task RefreshSharesInflightWorkAndPublishesOnlySuccessfulCatalogs()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var refreshCalls = 0;
        var release = new TaskCompletionSource<IReadOnlyList<GameModelDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = new GameMediaModelRegistry();
        registry.Register(Registration(
            "dynamic",
            new[] { Model("dynamic", "old", GameMediaKind.Image) },
            supportsDynamicModels: true,
            refresh: async (_, _) =>
            {
                Interlocked.Increment(ref refreshCalls);
                return await release.Task.ConfigureAwait(false);
            }));

        var first = registry.RefreshAsync("dynamic", testCancellation).AsTask();
        var second = registry.RefreshAsync("dynamic", testCancellation).AsTask();
        release.SetResult(new[] { Model("dynamic", "new", GameMediaKind.Video) });
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, refreshCalls);
        Assert.All(results, result => Assert.Equal(GameMediaModelRefreshStatus.Updated, result.Status));
        Assert.Null(registry.GetModel("dynamic", "old"));
        Assert.NotNull(registry.GetModel("dynamic", "new"));

        registry.Register(Registration(
            "dynamic",
            new[] { Model("dynamic", "stable", GameMediaKind.Audio) },
            supportsDynamicModels: true,
            refresh: (_, _) => throw new InvalidOperationException("offline")),
            replace: true);
        var failed = await registry.RefreshAsync("dynamic", testCancellation);
        Assert.Equal(GameMediaModelRefreshStatus.Failed, failed.Status);
        Assert.Equal("offline", failed.ErrorMessage);
        Assert.NotNull(registry.GetModel("dynamic", "stable"));
    }

    [Fact]
    public async Task CancelingOneRefreshWaiterDoesNotCancelSharedWork()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var refreshCalls = 0;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<GameModelDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = new GameMediaModelRegistry();
        registry.Register(Registration(
            "shared",
            new[] { Model("shared", "old", GameMediaKind.Image) },
            supportsDynamicModels: true,
            refresh: async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref refreshCalls);
                started.TrySetResult(true);
                return await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }));

        using var firstCancellation = new CancellationTokenSource();
        var first = registry.RefreshAsync("shared", firstCancellation.Token).AsTask();
        await started.Task.WaitAsync(testCancellation);
        var second = registry.RefreshAsync("shared", testCancellation).AsTask();
        firstCancellation.Cancel();

        var canceled = await first;
        Assert.Equal(GameMediaModelRefreshStatus.Canceled, canceled.Status);
        Assert.False(second.IsCompleted);

        release.SetResult(new[] { Model("shared", "new", GameMediaKind.Video) });
        var completed = await second;
        Assert.Equal(GameMediaModelRefreshStatus.Updated, completed.Status);
        Assert.Equal(1, refreshCalls);
        Assert.NotNull(registry.GetModel("shared", "new"));
    }

    [Fact]
    public async Task ReplacementCancelsAStaleRefreshWithoutPublishingItsModels()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = new GameMediaModelRegistry();
        registry.Register(Registration(
            "replaceable",
            new[] { Model("replaceable", "old", GameMediaKind.Image) },
            supportsDynamicModels: true,
            refresh: async (_, cancellationToken) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Array.Empty<GameModelDescriptor>();
            }));

        var refresh = registry.RefreshAsync("replaceable", testCancellation).AsTask();
        await started.Task;
        registry.Register(
            Registration("replaceable", new[] { Model("replaceable", "current", GameMediaKind.Video) }),
            replace: true);

        var result = await refresh;
        Assert.Equal(GameMediaModelRefreshStatus.StaleRegistration, result.Status);
        Assert.NotNull(registry.GetModel("replaceable", "current"));
        Assert.Null(registry.GetModel("replaceable", "old"));
    }

    [Fact]
    public async Task ResolvesSharedAuthenticationAndDispatchesByModelForAllMediaKinds()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var resolution = new GameProviderAuthResolution(
            new GameCredential(GameCredentialKind.BearerToken, "secret"),
            "test",
            new Uri("https://authenticated.example/v1"),
            new Dictionary<string, string?>
            {
                ["X-Shared"] = "auth",
                ["X-Auth"] = "yes",
                ["X-Remove"] = null,
            },
            new Dictionary<string, string> { ["region"] = "test" });
        var authentication = new TestAuthentication(true, resolution);
        var invocations = new List<GameMediaGenerationInvocation>();
        var progress = new List<GameMediaGenerationProgress>();
        using var registry = new GameMediaModelRegistry();
        registry.Register(Registration(
            "creator",
            new[]
            {
                Model("creator", "image", GameMediaKind.Image, headers: new Dictionary<string, string?>
                {
                    ["X-Shared"] = "model",
                    ["X-Model"] = "yes",
                    ["X-Remove"] = "model",
                }),
                Model("creator", "audio", GameMediaKind.Audio),
                Model("creator", "video", GameMediaKind.Video),
            },
            authentication: authentication,
            factory: invocation =>
            {
                invocations.Add(invocation);
                return new DelegateGenerator(async (request, report, cancellationToken) =>
                {
                    if (report is not null)
                    {
                        await report(new GameMediaGenerationProgress("working", 0.5), cancellationToken);
                    }

                    return Result(request.Kind);
                });
            }));

        foreach (var pair in new[]
                 {
                     (Model: "image", Kind: GameMediaKind.Image),
                     (Model: "audio", Kind: GameMediaKind.Audio),
                     (Model: "video", Kind: GameMediaKind.Video),
                 })
        {
            var generated = await registry.GenerateAsync(
                "creator",
                pair.Model,
                Request(pair.Kind),
                (update, _) =>
                {
                    progress.Add(update);
                    return ValueTask.CompletedTask;
                },
                testCancellation);
            Assert.Equal(GameMediaModelGenerationStatus.Completed, generated.Status);
            Assert.NotNull(generated.Result);
        }

        Assert.Equal(3, authentication.CheckCalls);
        Assert.Equal(3, authentication.ResolveCalls);
        Assert.Equal(3, invocations.Count);
        Assert.All(invocations, invocation => Assert.Equal(new Uri("https://authenticated.example/v1"), invocation.Endpoint));
        Assert.Equal("auth", invocations[0].Headers["X-Shared"]);
        Assert.Equal("yes", invocations[0].Headers["X-Model"]);
        Assert.Equal("yes", invocations[0].Headers["X-Auth"]);
        Assert.DoesNotContain("X-Remove", invocations[0].Headers.Keys);
        Assert.Equal("test", invocations[0].Configuration["region"]);
        Assert.Equal(3, progress.Count);
    }

    [Fact]
    public async Task UnknownModelsCapabilitiesAndAuthenticationFailInBand()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var invocations = 0;
        using var registry = new GameMediaModelRegistry();
        registry.Register(Registration(
            "images",
            new[] { Model("images", "draw", GameMediaKind.Image) },
            factory: _ =>
            {
                invocations++;
                return new DelegateGenerator((request, _, _) => new ValueTask<GameMediaGenerationResult>(Result(request.Kind)));
            }));

        var unknownProvider = await registry.GenerateAsync(
            "missing", "draw", Request(GameMediaKind.Image), cancellationToken: testCancellation);
        var unknownModel = await registry.GenerateAsync(
            "images", "missing", Request(GameMediaKind.Image), cancellationToken: testCancellation);
        var wrongKind = await registry.GenerateAsync(
            "images", "draw", Request(GameMediaKind.Video), cancellationToken: testCancellation);

        Assert.Equal("provider_not_found", unknownProvider.ErrorCode);
        Assert.Equal("model_not_found", unknownModel.ErrorCode);
        Assert.Equal("capability_mismatch", wrongKind.ErrorCode);
        Assert.Equal(0, invocations);

        registry.Register(Registration(
            "locked",
            new[] { Model("locked", "draw", GameMediaKind.Image) },
            authentication: new TestAuthentication(false, null)),
            replace: false);
        var unconfigured = await registry.GenerateAsync(
            "locked", "draw", Request(GameMediaKind.Image), cancellationToken: testCancellation);
        Assert.Equal(GameMediaModelGenerationStatus.Failed, unconfigured.Status);
        Assert.Equal("authentication_unconfigured", unconfigured.ErrorCode);
    }

    [Fact]
    public async Task UnknownSourceMediaTypesFailClosedBeforeProviderInvocation()
    {
        var invocations = 0;
        using var registry = new GameMediaModelRegistry();
        registry.Register(Registration(
            "images",
            new[]
            {
                Model(
                    "images",
                    "edit",
                    GameMediaKind.Image,
                    GameModelInputCapabilities.Text | GameModelInputCapabilities.Image),
            },
            factory: _ =>
            {
                invocations++;
                return new DelegateGenerator((request, _, _) =>
                    new ValueTask<GameMediaGenerationResult>(Result(request.Kind)));
            }));

        var result = await registry.GenerateAsync(
            "images",
            "edit",
            new GameMediaGenerationRequest(
                "unknown-source",
                GameMediaKind.Image,
                "{}",
                prompt: "edit",
                sources: new[] { new ResourceContent("memory://unknown", "application/octet-stream") }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, result.Status);
        Assert.Equal("capability_mismatch", result.ErrorCode);
        Assert.Equal(0, invocations);
    }

    [Fact]
    public async Task CancellationAndTimeoutReturnTerminalResultsEvenWhenAGeneratorIgnoresTokens()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var completion = new TaskCompletionSource<GameMediaGenerationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var generationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = new GameMediaModelRegistry(new GameMediaModelRegistryOptions
        {
            GenerationTimeout = TimeSpan.FromMilliseconds(100),
        });
        registry.Register(Registration(
            "slow",
            new[] { Model("slow", "render", GameMediaKind.Video) },
            factory: _ => new DelegateGenerator((_, _, _) =>
            {
                generationStarted.TrySetResult(true);
                return new ValueTask<GameMediaGenerationResult>(completion.Task);
            })));

        using var canceled = new CancellationTokenSource();
        var canceledGeneration = registry.GenerateAsync(
            "slow",
            "render",
            Request(GameMediaKind.Video),
            cancellationToken: canceled.Token).AsTask();
        await generationStarted.Task.WaitAsync(testCancellation);
        canceled.Cancel();
        var canceledResult = await canceledGeneration;
        Assert.Equal(GameMediaModelGenerationStatus.Canceled, canceledResult.Status);
        Assert.Equal("canceled", canceledResult.ErrorCode);

        var timeoutResult = await registry.GenerateAsync(
            "slow", "render", Request(GameMediaKind.Video), cancellationToken: testCancellation);
        Assert.Equal(GameMediaModelGenerationStatus.Failed, timeoutResult.Status);
        Assert.Equal("timeout", timeoutResult.ErrorCode);
        completion.TrySetResult(Result(GameMediaKind.Video));
    }

    [Fact]
    public async Task RefreshAndProgressCallbackTimeoutsFailInBand()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var refreshCompletion = new TaskCompletionSource<IReadOnlyList<GameModelDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progressCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = new GameMediaModelRegistry(new GameMediaModelRegistryOptions
        {
            RefreshTimeout = TimeSpan.FromMilliseconds(100),
            ProgressCallbackTimeout = TimeSpan.FromMilliseconds(100),
        });
        registry.Register(Registration(
            "timeouts",
            new[] { Model("timeouts", "draw", GameMediaKind.Image) },
            supportsDynamicModels: true,
            refresh: (_, _) => new ValueTask<IReadOnlyList<GameModelDescriptor>>(refreshCompletion.Task),
            factory: _ => new DelegateGenerator(async (request, report, cancellationToken) =>
            {
                if (report is not null)
                {
                    await report(new GameMediaGenerationProgress("working"), cancellationToken);
                }

                return Result(request.Kind);
            })));

        var refresh = await registry.RefreshAsync("timeouts", testCancellation);
        Assert.Equal(GameMediaModelRefreshStatus.Failed, refresh.Status);
        Assert.Contains("timed out", refresh.ErrorMessage, StringComparison.Ordinal);
        Assert.NotNull(registry.GetModel("timeouts", "draw"));

        var generated = await registry.GenerateAsync(
            "timeouts",
            "draw",
            Request(GameMediaKind.Image),
            (_, _) => new ValueTask(progressCompletion.Task),
            testCancellation);
        Assert.Equal(GameMediaModelGenerationStatus.Failed, generated.Status);
        Assert.Equal("generation_failed", generated.ErrorCode);
        Assert.Contains("progress callback timed out", generated.ErrorMessage, StringComparison.Ordinal);
        refreshCompletion.TrySetResult(Array.Empty<GameModelDescriptor>());
        progressCompletion.TrySetResult(true);
    }

    [Fact]
    public async Task RequestResultAndProgressLimitsFailInBand()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        using var registry = new GameMediaModelRegistry(new GameMediaModelRegistryOptions
        {
            MaxSources = 1,
            MaxOutputs = 1,
            MaxProgressEvents = 1,
            MaxJsonBytes = 128,
        });
        registry.Register(Registration(
            "bounded",
            new[]
            {
                Model(
                    "bounded",
                    "draw",
                    GameMediaKind.Image,
                    inputs: GameModelInputCapabilities.Text | GameModelInputCapabilities.Image),
            },
            factory: _ => new DelegateGenerator(async (_, report, cancellationToken) =>
            {
                if (report is not null)
                {
                    await report(new GameMediaGenerationProgress("one"), cancellationToken);
                    await report(new GameMediaGenerationProgress("two"), cancellationToken);
                }

                return new GameMediaGenerationResult(new[]
                {
                    new ResourceContent("memory://one", "image/png"),
                    new ResourceContent("memory://two", "image/png"),
                });
            })));

        var tooManySources = await registry.GenerateAsync(
            "bounded",
            "draw",
            new GameMediaGenerationRequest(
                "many",
                GameMediaKind.Image,
                "{}",
                sources: new[]
                {
                    new ResourceContent("memory://one", "image/png"),
                    new ResourceContent("memory://two", "image/png"),
                }),
            cancellationToken: testCancellation);
        Assert.Equal("request_limit", tooManySources.ErrorCode);

        var oversizedJson = await registry.GenerateAsync(
            "bounded",
            "draw",
            new GameMediaGenerationRequest(
                "oversized",
                GameMediaKind.Image,
                System.Text.Json.JsonSerializer.Serialize(new { value = new string('x', 200) })),
            cancellationToken: testCancellation);
        Assert.Equal("request_limit", oversizedJson.ErrorCode);

        var progressFailure = await registry.GenerateAsync(
            "bounded",
            "draw",
            Request(GameMediaKind.Image),
            (_, _) => ValueTask.CompletedTask,
            testCancellation);
        Assert.Equal("generation_failed", progressFailure.ErrorCode);
        Assert.Contains("progress event limit", progressFailure.ErrorMessage, StringComparison.Ordinal);

        registry.Register(Registration(
            "bounded",
            new[] { Model("bounded", "draw", GameMediaKind.Image) },
            factory: _ => new DelegateGenerator((_, _, _) =>
                new ValueTask<GameMediaGenerationResult>(new GameMediaGenerationResult(new[]
                {
                    new ResourceContent("memory://one", "image/png"),
                    new ResourceContent("memory://two", "image/png"),
                })))),
            replace: true);
        var invalidResult = await registry.GenerateAsync(
            "bounded", "draw", Request(GameMediaKind.Image), cancellationToken: testCancellation);
        Assert.Equal("invalid_result", invalidResult.ErrorCode);
    }

    [Fact]
    public async Task RefreshAllIsConcurrentAndBestEffort()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        using var registry = new GameMediaModelRegistry();
        registry.Register(Registration(
            "good",
            Array.Empty<GameModelDescriptor>(),
            supportsDynamicModels: true,
            refresh: (_, _) => new ValueTask<IReadOnlyList<GameModelDescriptor>>(
                new[] { Model("good", "voice", GameMediaKind.Audio) })));
        registry.Register(Registration(
            "bad",
            new[] { Model("bad", "stable", GameMediaKind.Video) },
            supportsDynamicModels: true,
            refresh: (_, _) => throw new InvalidOperationException("unavailable")));

        var results = await registry.RefreshAsync(cancellationToken: testCancellation);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.ProviderId == "good" && item.Status == GameMediaModelRefreshStatus.Updated);
        Assert.Contains(results, item => item.ProviderId == "bad" && item.Status == GameMediaModelRefreshStatus.Failed);
        Assert.NotNull(registry.GetModel("bad", "stable"));
    }

    private static GameMediaProviderRegistration Registration(
        string providerId,
        IReadOnlyList<GameModelDescriptor> models,
        bool supportsDynamicModels = false,
        GameMediaModelRefresh? refresh = null,
        IGameProviderAuthentication? authentication = null,
        GameMediaGeneratorFactory? factory = null) =>
        new(
            new GameProviderDescriptor(
                providerId,
                endpoint: new Uri("https://provider.example/v1"),
                supportsDynamicModels: supportsDynamicModels),
            authentication ?? new StaticGameProviderAuthentication(),
            factory ?? (_ => new DelegateGenerator((request, _, _) =>
                new ValueTask<GameMediaGenerationResult>(Result(request.Kind)))),
            models,
            refresh);

    private static GameModelDescriptor Model(
        string providerId,
        string modelId,
        GameMediaKind kind,
        GameModelInputCapabilities inputs = GameModelInputCapabilities.Text,
        IReadOnlyDictionary<string, string?>? headers = null) =>
        new(
            providerId,
            modelId,
            inputCapabilities: inputs,
            outputCapabilities: kind switch
            {
                GameMediaKind.Image => GameModelOutputCapabilities.Image,
                GameMediaKind.Audio => GameModelOutputCapabilities.Audio,
                GameMediaKind.Video => GameModelOutputCapabilities.Video,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            api: "media-test",
            baseUrl: new Uri("https://model.example/v1"),
            headers: headers);

    private static GameMediaGenerationRequest Request(GameMediaKind kind) =>
        new("request", kind, "{}", prompt: "create media");

    private static GameMediaGenerationResult Result(GameMediaKind kind) =>
        new(
            new[]
            {
                new ResourceContent(
                    "memory://generated",
                    kind switch
                    {
                        GameMediaKind.Image => "image/png",
                        GameMediaKind.Audio => "audio/wav",
                        GameMediaKind.Video => "video/mp4",
                        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                    }),
            },
            "{\"usage\":1}",
            "provider-request");

    private sealed class DelegateGenerator : IGameMediaGenerator
    {
        private readonly Func<
            GameMediaGenerationRequest,
            GameMediaProgressHandler?,
            CancellationToken,
            ValueTask<GameMediaGenerationResult>> _generate;

        public DelegateGenerator(Func<
            GameMediaGenerationRequest,
            GameMediaProgressHandler?,
            CancellationToken,
            ValueTask<GameMediaGenerationResult>> generate)
        {
            _generate = generate;
        }

        public ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken) =>
            _generate(request, progress, cancellationToken);
    }

    private sealed class TestAuthentication : IGameProviderAuthentication
    {
        private readonly bool _configured;
        private readonly GameProviderAuthResolution? _resolution;

        public TestAuthentication(bool configured, GameProviderAuthResolution? resolution)
        {
            _configured = configured;
            _resolution = resolution;
        }

        public int CheckCalls { get; private set; }

        public int ResolveCalls { get; private set; }

        public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

        public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCalls++;
            return new ValueTask<GameProviderAuthStatus>(new GameProviderAuthStatus(
                _configured,
                "test",
                error: _configured ? null : "not configured"));
        }

        public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCalls++;
            return new ValueTask<GameProviderAuthResolution?>(_resolution);
        }

        public ValueTask<GameCredential> LoginAsync(
            string scheme,
            GameAuthInteraction interaction,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }
}
