using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public void CredentialKeysExposeConsistentValueOperators()
    {
        var key = new GameCredentialKey("provider", "profile");

        Assert.True(key == new GameCredentialKey("provider", "profile"));
        Assert.True(key != new GameCredentialKey("provider", "other"));
    }

    [Fact]
    public void DescriptorClampsReasoningAndResolutionBoundsParametersAndCost()
    {
        var provider = new ScriptedProvider();
        var model = Model(
            "provider",
            "model",
            maximumOutputTokens: 2_000,
            reasoningLevels: new[] { GameReasoningLevel.Low, GameReasoningLevel.High },
            cost: new GameModelCost(1, 2, 0.25m, 1.25m),
            reasoningLevelValues: new Dictionary<GameReasoningLevel, string>
            {
                [GameReasoningLevel.Low] = "provider-low",
            });
        var catalog = Catalog(Registration("provider", provider, model));

        var resolution = catalog.Resolve(
            "provider",
            "model",
            GameReasoningLevel.Minimal,
            requiredInput: GameModelInputCapabilities.StructuredData,
            requiredOutput: GameModelOutputCapabilities.ToolCalls);
        var parameters = resolution.CreateParameters(new ModelParameters { MaxOutputTokens = 10_000 });

        Assert.Equal(GameReasoningLevel.Low, resolution.Reasoning);
        Assert.Equal("provider-low", parameters.ReasoningLevel);
        Assert.Equal(2_000, parameters.MaxOutputTokens);
        Assert.Equal(4.5m, resolution.EstimateCost(new ModelUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000)));
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve(
            "provider",
            "model",
            requiredInput: GameModelInputCapabilities.Video));
    }

    [Fact]
    public async Task RefreshOverlaysBaselineAndDetectsEveryDescriptorChange()
    {
        var provider = new ScriptedProvider();
        var dynamic = Model("provider", "shared", displayName: "dynamic", cost: new GameModelCost(1));
        var registration = Registration(
            "provider",
            provider,
            new[] { Model("provider", "shared", displayName: "baseline"), Model("provider", "static") },
            refresh: (_, _) => new ValueTask<IReadOnlyList<GameModelDescriptor>>(new[] { dynamic }));
        var catalog = Catalog(registration);

        var first = await catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken);
        var second = await catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameModelRefreshStatus.Updated, first.Status);
        Assert.Equal(GameModelRefreshStatus.Unchanged, second.Status);
        Assert.Equal(new[] { "dynamic", "static" }, catalog.GetModels("provider").Select(model => model.DisplayName));

        dynamic = Model("provider", "shared", displayName: "dynamic", cost: new GameModelCost(2));
        var costChange = await catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GameModelRefreshStatus.Updated, costChange.Status);
    }

    [Fact]
    public async Task ReplacingProviderSupersedesAnInFlightRefreshEvenWhenItsSourceIgnoresCancellation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Registration(
            "provider",
            new ScriptedProvider(),
            new[] { Model("provider", "old") },
            async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return new[] { Model("provider", "stale") };
            });
        var catalog = Catalog(old);

        var refresh = catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        catalog.Register(Registration("provider", new ScriptedProvider(), Model("provider", "new")), replace: true);
        release.TrySetResult(true);

        var result = await refresh;
        Assert.Equal(GameModelRefreshStatus.StaleRegistration, result.Status);
        Assert.Equal("new", Assert.Single(catalog.GetModels("provider")).ModelId);
    }

    [Fact]
    public async Task ThrowingRefreshCancellationCallbacksCannotBlockProviderReplacement()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Registration(
            "provider",
            new ScriptedProvider(),
            new[] { Model("provider", "old") },
            async (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(
                    () => throw new InvalidOperationException("callback failed"));
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return Array.Empty<GameModelDescriptor>();
            });
        var catalog = Catalog(old);

        var refresh = catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var exception = Record.Exception(
            () => catalog.Register(
                Registration("provider", new ScriptedProvider(), Model("provider", "new")),
                replace: true));

        Assert.Null(exception);
        Assert.Equal(GameModelRefreshStatus.StaleRegistration, (await refresh).Status);
        Assert.Equal("new", Assert.Single(catalog.GetModels("provider")).ModelId);
    }

    [Fact]
    public async Task SupersededRefreshCannotCommitStaleModelsWhenStorageIgnoresCancellation()
    {
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var store = new IgnoringCancellationCatalogStore();
        var catalog = new GameModelCatalog(store: store);
        catalog.Register(Registration(
            "provider",
            new ScriptedProvider(),
            Array.Empty<GameModelDescriptor>(),
            async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.TrySetResult(true);
                    await releaseFirst.Task.ConfigureAwait(false);
                    return new[] { Model("provider", "stale") };
                }

                secondEntered.TrySetResult(true);
                await releaseSecond.Task.ConfigureAwait(false);
                return new[] { Model("provider", "newest") };
            }));

        var first = catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        releaseFirst.TrySetResult(true);
        Assert.Equal(GameModelRefreshStatus.StaleRegistration, (await first).Status);
        await secondEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseSecond.TrySetResult(true);

        Assert.Equal(GameModelRefreshStatus.Updated, (await second).Status);
        Assert.Equal("newest", Assert.Single(catalog.GetModels("provider")).ModelId);
        Assert.Equal("newest", Assert.Single((await store.LoadAsync("provider", TestContext.Current.CancellationToken))!.Models).ModelId);
    }

    [Fact]
    public async Task CachedDynamicModelsRestoreBeforeAuthenticationOrNetworkAccess()
    {
        var store = new InMemoryGameModelCatalogStore();
        var writer = new GameModelCatalog(store: store, clock: () => DateTimeOffset.UnixEpoch);
        writer.Register(Registration(
            "provider",
            new ScriptedProvider(),
            Array.Empty<GameModelDescriptor>(),
            (_, _) => new ValueTask<IReadOnlyList<GameModelDescriptor>>(new[] { Model("provider", "cached") })));
        Assert.Equal(
            GameModelRefreshStatus.Updated,
            (await writer.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken)).Status);

        var fetches = 0;
        var reader = new GameModelCatalog(store: store);
        reader.Register(new GameModelProviderRegistration(
            new GameProviderDescriptor("provider", supportsDynamicModels: true),
            new ScriptedProvider(),
            new StaticGameProviderAuthentication(configured: false),
            refreshModels: (_, _) =>
            {
                Interlocked.Increment(ref fetches);
                return new ValueTask<IReadOnlyList<GameModelDescriptor>>(Array.Empty<GameModelDescriptor>());
            }));

        var result = await reader.RefreshAsync(
            "provider",
            allowNetwork: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameModelRefreshStatus.SkippedUnconfigured, result.Status);
        Assert.Equal("cached", Assert.Single(reader.GetModels("provider")).ModelId);
        Assert.Equal(0, fetches);
    }

    [Fact]
    public async Task ConcurrentCatalogInstancesCannotOverwriteTheSameStoredRevision()
    {
        var store = new InMemoryGameModelCatalogStore();
        var bothEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        GameModelRefresh Refresh(string modelId) => async (_, _) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
            {
                bothEntered.TrySetResult(true);
            }

            await release.Task.ConfigureAwait(false);
            return new[] { Model("provider", modelId) };
        };

        var first = new GameModelCatalog(store: store);
        var second = new GameModelCatalog(store: store);
        first.Register(Registration(
            "provider",
            new ScriptedProvider(),
            Array.Empty<GameModelDescriptor>(),
            Refresh("first")));
        second.Register(Registration(
            "provider",
            new ScriptedProvider(),
            Array.Empty<GameModelDescriptor>(),
            Refresh("second")));

        var firstRefresh = first.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        var secondRefresh = second.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await bothEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.TrySetResult(true);
        var results = await Task.WhenAll(firstRefresh, secondRefresh);

        Assert.Contains(results, result => result.Status == GameModelRefreshStatus.Updated);
        Assert.Contains(results, result => result.Status == GameModelRefreshStatus.StoreConflict);
        Assert.Equal(1, (await store.LoadAsync("provider", TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task StoredAuthenticationSerializesRefreshAndDurablyCommitsLogin()
    {
        var store = new InMemoryGameCredentialStore();
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var key = new GameCredentialKey("provider");
        await store.SetAsync(
            key,
            new GameCredential(GameCredentialKind.OAuth, "expired", now.AddMinutes(-1)),
            TestContext.Current.CancellationToken);
        var refreshes = 0;
        var authentication = new StoredGameProviderAuthentication(
            "provider",
            store,
            schemes: new[] { "oauth" },
            login: (_, _, _) => new ValueTask<GameCredential>(
                new GameCredential(GameCredentialKind.OAuth, "logged-in", now.AddHours(1))),
            refresh: (_, _) =>
            {
                Interlocked.Increment(ref refreshes);
                return new ValueTask<GameCredential>(
                    new GameCredential(GameCredentialKind.OAuth, "refreshed", now.AddHours(1)));
            },
            clock: () => now,
            refreshSkew: TimeSpan.Zero);

        var statusBeforeRefresh = await authentication.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(statusBeforeRefresh.Configured);

        var resolved = await Task.WhenAll(
            authentication.ResolveAsync(TestContext.Current.CancellationToken).AsTask(),
            authentication.ResolveAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, refreshes);
        Assert.All(resolved, value => Assert.Equal("refreshed", value!.Credential.Secret));
        var login = await authentication.LoginAsync(
            "oauth",
            new GameAuthInteraction(),
            TestContext.Current.CancellationToken);
        Assert.Equal("logged-in", login.Secret);
        Assert.Equal("logged-in", (await store.GetAsync(key, TestContext.Current.CancellationToken))!.Secret);
        Assert.DoesNotContain("logged-in", login.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new GameCredential(GameCredentialKind.ApiKey, "unsafe\r\nvalue"));
    }

    [Fact]
    public void CredentialExpiryHandlesBoundaryClocksWithoutOverflow()
    {
        var credential = new GameCredential(
            GameCredentialKind.DeveloperHostedToken,
            "short-lived",
            DateTimeOffset.MaxValue);

        Assert.True(credential.IsExpired(DateTimeOffset.MaxValue.AddMinutes(-30), TimeSpan.FromHours(1)));
        Assert.True(credential.IsExpired(DateTimeOffset.MaxValue, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => credential.IsExpired(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task StoredAuthenticationNeverCommitsExpiredLoginOrRefreshResults()
    {
        var store = new InMemoryGameCredentialStore();
        var now = DateTimeOffset.UnixEpoch.AddHours(4);
        var key = new GameCredentialKey("provider");
        var original = new GameCredential(GameCredentialKind.OAuth, "original", now.AddMinutes(-1));
        await store.SetAsync(key, original, TestContext.Current.CancellationToken);
        var authentication = new StoredGameProviderAuthentication(
            "provider",
            store,
            schemes: new[] { "oauth" },
            login: (_, _, _) => new ValueTask<GameCredential>(
                new GameCredential(GameCredentialKind.OAuth, "expired-login", now)),
            refresh: (_, _) => new ValueTask<GameCredential>(
                new GameCredential(GameCredentialKind.OAuth, "expired-refresh", now)),
            clock: () => now,
            refreshSkew: TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await authentication.ResolveAsync(TestContext.Current.CancellationToken));
        Assert.Same(original, await store.GetAsync(key, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await authentication.LoginAsync("oauth", new GameAuthInteraction(), TestContext.Current.CancellationToken));
        Assert.Same(original, await store.GetAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnvironmentAuthenticationResolvesPerRequestWithoutExposingSecretsInStatus()
    {
        var value = "first";
        var authentication = new EnvironmentGameProviderAuthentication(
            "GAME_MODEL_KEY",
            read: _ => value);

        var status = await authentication.CheckAsync(TestContext.Current.CancellationToken);
        var first = await authentication.ResolveAsync(TestContext.Current.CancellationToken);
        value = "second";
        var second = await authentication.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.True(status.Configured);
        Assert.DoesNotContain("first", status.Source, StringComparison.Ordinal);
        Assert.Equal("first", first!.Credential.Secret);
        Assert.Equal("second", second!.Credential.Secret);
        Assert.DoesNotContain("second", second.Credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidEnvironmentCredentialFailsAvailabilityCheckWithoutLeakingItsValue()
    {
        const string malformed = "secret\r\nheader";
        var authentication = new EnvironmentGameProviderAuthentication(
            "GAME_MODEL_KEY",
            read: _ => malformed);

        var status = await authentication.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Configured);
        Assert.Contains("invalid credential", status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(malformed, status.Error, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await authentication.ResolveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CatalogDispatchRefreshesAnExpiredCredentialBeforeStreaming()
    {
        var store = new InMemoryGameCredentialStore();
        var now = DateTimeOffset.UnixEpoch.AddHours(2);
        await store.SetAsync(
            new GameCredentialKey("provider"),
            new GameCredential(GameCredentialKind.OAuth, "expired", now.AddMinutes(-1)),
            TestContext.Current.CancellationToken);
        var refreshes = 0;
        var authentication = new StoredGameProviderAuthentication(
            "provider",
            store,
            refresh: (_, _) =>
            {
                Interlocked.Increment(ref refreshes);
                return new ValueTask<GameCredential>(
                    new GameCredential(GameCredentialKind.OAuth, "fresh", now.AddHours(1)));
            },
            clock: () => now,
            refreshSkew: TimeSpan.Zero);
        var provider = new ScriptedProvider();
        string? streamedSecret = null;
        var catalog = Catalog(new GameModelProviderRegistration(
            new GameProviderDescriptor("provider"),
            provider,
            authentication,
            new[] { Model("provider", "model") },
            stream: (request, resolved, cancellationToken) => CaptureSecret(
                provider,
                request,
                resolved,
                value => streamedSecret = value,
                cancellationToken)));
        var extension = new GameModelCatalogExtension(catalog);
        await using var runtime = new GameAgentBuilder(new ScriptedProvider(), "fallback")
            .UseModelSelector((_, _) => new ValueTask<GameModelSelection?>(extension.Select("provider", "model")))
            .UseExtension(extension)
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "event", "{}", new GameMoment("world", 1), inputId: "refresh-input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, refreshes);
        Assert.Equal("fresh", streamedSecret);
    }

    [Fact]
    public async Task CatalogExtensionResolvesAuthPerTurnAndAppliesSelectedModelParameters()
    {
        var fallback = new ScriptedProvider();
        var underlying = new ScriptedProvider();
        var seenSecrets = new ConcurrentQueue<string?>();
        var catalog = Catalog(new GameModelProviderRegistration(
            new GameProviderDescriptor("catalog-provider"),
            underlying,
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.DeveloperHostedToken, "short-lived")),
            new[]
            {
                Model(
                    "catalog-provider",
                    "capable",
                    maximumOutputTokens: 512,
                    reasoningLevels: new[] { GameReasoningLevel.High }),
            },
            stream: (request, authentication, cancellationToken) => Capture(
                underlying,
                request,
                authentication,
                seenSecrets,
                cancellationToken)));
        var extension = new GameModelCatalogExtension(catalog);
        var selectedModel = extension.Select(
            "catalog-provider",
            "capable",
            GameReasoningLevel.Medium,
            new ModelParameters { MaxOutputTokens = 4_096 });
        Assert.Equal(2_048, selectedModel.ContextWindowTokens);
        Assert.Equal(512, selectedModel.MaximumOutputTokens);
        await using var runtime = new GameAgentBuilder(fallback, "fallback")
            .UseModelSelector((_, _) => new ValueTask<GameModelSelection?>(selectedModel))
            .UseExtension(extension)
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "event", "{}", new GameMoment("world", 1), inputId: "input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Empty(fallback.Requests);
        var request = Assert.Single(underlying.Requests);
        Assert.Equal("capable", request.Model);
        Assert.Equal("high", request.Parameters.ReasoningLevel);
        Assert.Equal(512, request.Parameters.MaxOutputTokens);
        Assert.Equal("short-lived", Assert.Single(seenSecrets));
    }

    [Fact]
    public async Task CatalogExtensionCanSelectAProviderRegisteredAfterRuntimeConstruction()
    {
        var fallback = new ScriptedProvider();
        var selected = new ScriptedProvider();
        var catalog = new GameModelCatalog();
        var extension = new GameModelCatalogExtension(catalog);
        await using var runtime = new GameAgentBuilder(fallback, "fallback")
            .UseModelSelector((_, _) => new ValueTask<GameModelSelection?>(extension.Select("late", "model")))
            .UseExtension(extension)
            .Build();
        catalog.Register(Registration("late", selected, Model("late", "model")));

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "event", "{}", new GameMoment("world", 1), inputId: "late-input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Empty(fallback.Requests);
        Assert.Single(selected.Requests);
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Capture(
        ScriptedProvider provider,
        ModelRequest request,
        GameProviderAuthResolution? authentication,
        ConcurrentQueue<string?> secrets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        secrets.Enqueue(authentication?.Credential.Secret);
        await foreach (var streamEvent in provider.StreamAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return streamEvent;
        }
    }

    private static async IAsyncEnumerable<ModelStreamEvent> CaptureSecret(
        ScriptedProvider provider,
        ModelRequest request,
        GameProviderAuthResolution? authentication,
        Action<string?> capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        capture(authentication?.Credential.Secret);
        await foreach (var streamEvent in provider.StreamAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return streamEvent;
        }
    }

    private static GameModelCatalog Catalog(params GameModelProviderRegistration[] registrations)
    {
        var catalog = new GameModelCatalog();
        foreach (var registration in registrations)
        {
            catalog.Register(registration);
        }

        return catalog;
    }

    private static GameModelProviderRegistration Registration(
        string providerId,
        IModelProvider provider,
        GameModelDescriptor model) =>
        Registration(providerId, provider, new[] { model });

    private static GameModelProviderRegistration Registration(
        string providerId,
        IModelProvider provider,
        IReadOnlyList<GameModelDescriptor> models,
        GameModelRefresh? refresh = null) =>
        new(
            new GameProviderDescriptor(providerId, supportsDynamicModels: refresh is not null),
            provider,
            new StaticGameProviderAuthentication(),
            models,
            refresh);

    private static GameModelDescriptor Model(
        string providerId,
        string modelId,
        string? displayName = null,
        int maximumOutputTokens = 0,
        IReadOnlyCollection<GameReasoningLevel>? reasoningLevels = null,
        GameModelCost? cost = null,
        IReadOnlyDictionary<GameReasoningLevel, string>? reasoningLevelValues = null) =>
        new(
            providerId,
            modelId,
            displayName,
            contextWindowTokens: maximumOutputTokens == 0 ? 0 : maximumOutputTokens * 4,
            maximumOutputTokens,
            outputCapabilities: GameModelOutputCapabilities.Text
                | GameModelOutputCapabilities.ToolCalls
                | GameModelOutputCapabilities.Reasoning,
            reasoningLevels: reasoningLevels,
            cost: cost,
            reasoningLevelValues: reasoningLevelValues);

    private sealed class ScriptedProvider : IModelProvider
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            var partial = new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending);
            yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, partial);
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("ok") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
        }
    }

    private sealed class IgnoringCancellationCatalogStore : IGameModelCatalogStore
    {
        private readonly object _gate = new();
        private GameStoredModelCatalog? _catalog;

        public ValueTask<GameStoredModelCatalog?> LoadAsync(
            string providerId,
            CancellationToken cancellationToken)
        {
            _ = providerId;
            _ = cancellationToken;
            lock (_gate)
            {
                return new ValueTask<GameStoredModelCatalog?>(_catalog);
            }
        }

        public ValueTask<GameModelCatalogSaveResult> SaveAsync(
            GameStoredModelCatalog catalog,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            lock (_gate)
            {
                var revision = _catalog?.Revision ?? 0;
                if (revision != expectedRevision)
                {
                    return new ValueTask<GameModelCatalogSaveResult>(
                        new GameModelCatalogSaveResult(GameModelCatalogSaveStatus.Conflict, revision));
                }

                var nextRevision = checked(revision + 1);
                _catalog = new GameStoredModelCatalog(
                    catalog.ProviderId,
                    catalog.CatalogVersion,
                    catalog.Models,
                    catalog.CheckedAt,
                    nextRevision);
                return new ValueTask<GameModelCatalogSaveResult>(
                    new GameModelCatalogSaveResult(GameModelCatalogSaveStatus.Saved, nextRevision));
            }
        }
    }
}
