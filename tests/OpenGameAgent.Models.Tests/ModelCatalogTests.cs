using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenGameAgent.Attachments;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public void ProviderAndModelEndpointsRejectNonHttpAndAmbiguousUris()
    {
        Assert.Throws<ArgumentException>(() => new GameProviderDescriptor(
            "provider",
            endpoint: new Uri("file:///tmp/provider")));
        Assert.Throws<ArgumentException>(() => new GameProviderDescriptor(
            "provider",
            endpoint: new Uri("https://provider.example/v1#fragment")));
        Assert.Throws<ArgumentException>(() => new GameModelDescriptor(
            "provider",
            "model",
            baseUrl: new Uri("https://user:secret@provider.example/v1")));
        Assert.Throws<ArgumentException>(() => new GameModelDescriptor(
            "provider",
            "model",
            baseUrl: new Uri("https://provider.example/v1#fragment")));
    }

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
    public async Task DispatchProviderPreflightsImageCapabilityWithoutProviderIo()
    {
        var provider = new ScriptedProvider();
        var catalog = Catalog(Registration("provider", provider, Model("provider", "text-only")));
        var dispatch = catalog.CreateProvider("provider");
        var preflight = Assert.IsAssignableFrom<IModelRequestPreflight>(dispatch);
        var request = new ModelRequest(
            "text-only",
            "",
            new[]
            {
                new AgentMessage(
                    AgentRole.User,
                    new AgentContent[]
                    {
                        new ImageAttachmentContent(new GameImageAttachment(
                            "sha256:" + new string('a', 64),
                            GameImageMediaTypes.Png,
                            1,
                            1,
                            1)),
                    },
                    DateTimeOffset.UnixEpoch),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            "session",
            "run",
            1);

        await Assert.ThrowsAsync<ModelProviderException>(() => preflight.ValidateRequestAsync(
            request,
            TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public void PricingDistinguishesUnknownFromKnownFreeAndEstimatesItemizedUsage()
    {
        var unknown = Catalog(Registration(
            "provider",
            new ScriptedProvider(),
            Model("provider", "unknown", cost: new GameModelCost())))
            .Resolve("provider", "unknown");
        Assert.False(unknown.Model.Cost.IsKnown);
        Assert.Null(unknown.EstimateCostOrNull(new ModelUsage(1, 1)));
        Assert.Throws<ArgumentNullException>(() => unknown.EstimateCostOrNull(null!));
        Assert.Throws<InvalidOperationException>(() => unknown.EstimateCost(new ModelUsage(1, 1)));

        var free = new GameModelCost(isKnown: true);
        Assert.True(free.IsKnown);
        Assert.True(free.Estimate(new ModelUsage(1, 1)).IsKnown);
        Assert.Equal(0, free.Estimate(new ModelUsage(1, 1)).Total);

        var priced = new GameModelCost(1, 2, 0.5m, 1.5m, tiers: null, isKnown: true);
        var estimate = priced.Estimate(new ModelUsage(
            inputTokens: 5,
            outputTokens: 4,
            cacheReadTokens: 3,
            cacheWriteTokens: 2,
            reasoningTokens: 1,
            cacheWriteOneHourTokens: 1));
        Assert.True(estimate.IsKnown);
        Assert.Equal(0.000005, estimate.Input, 10);
        Assert.Equal(0.000008, estimate.Output, 10);
        Assert.Equal(0.0000015, estimate.CacheRead, 10);
        Assert.Equal(0.0000035, estimate.CacheWrite, 10);
    }

    [Fact]
    public void DescriptorPreservesAlwaysThinkingAndProviderSpecificOffValues()
    {
        var alwaysThinking = Model(
            "provider",
            "always",
            reasoningLevels: new[] { GameReasoningLevel.High, GameReasoningLevel.Maximum },
            reasoningLevelValues: new Dictionary<GameReasoningLevel, string>
            {
                [GameReasoningLevel.High] = "HIGH",
                [GameReasoningLevel.Maximum] = "MAXIMUM",
            });
        Assert.DoesNotContain(GameReasoningLevel.Off, alwaysThinking.ReasoningLevels);
        Assert.Equal(GameReasoningLevel.High, alwaysThinking.ClampReasoning(GameReasoningLevel.Off));
        Assert.Throws<InvalidOperationException>(() => alwaysThinking.GetReasoningValue(GameReasoningLevel.Off));

        var switchable = Model(
            "provider",
            "switchable",
            reasoningLevels: new[] { GameReasoningLevel.Off, GameReasoningLevel.Low },
            reasoningLevelValues: new Dictionary<GameReasoningLevel, string>
            {
                [GameReasoningLevel.Off] = "none",
                [GameReasoningLevel.Low] = "LOW",
            });
        Assert.Equal("none", switchable.GetReasoningValue(GameReasoningLevel.Off));
        Assert.Equal("LOW", switchable.GetReasoningValue(GameReasoningLevel.Low));

        var nonReasoning = new GameModelDescriptor("provider", "plain");
        Assert.Equal(new[] { GameReasoningLevel.Off }, nonReasoning.ReasoningLevels);
        Assert.Null(nonReasoning.GetReasoningValue(GameReasoningLevel.Off));
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
    public async Task RefreshStatusDetectsEveryBehaviorRelevantDescriptorField()
    {
        var changes = new (string Field, Func<GameModelDescriptor> Create)[]
        {
            (nameof(GameModelDescriptor.ModelId), () => ComparableModel(modelId: "changed-model")),
            (nameof(GameModelDescriptor.DisplayName), () => ComparableModel(displayName: "Changed model")),
            (nameof(GameModelDescriptor.Api), () => ComparableModel(api: "openai-responses")),
            (nameof(GameModelDescriptor.BaseUrl), () => ComparableModel(baseUrl: "https://changed.example/v1")),
            (nameof(GameModelDescriptor.ContextWindowTokens), () => ComparableModel(contextWindowTokens: 120_000)),
            (nameof(GameModelDescriptor.MaximumOutputTokens), () => ComparableModel(maximumOutputTokens: 12_000)),
            (nameof(GameModelDescriptor.InputCapabilities), () => ComparableModel(
                inputCapabilities: GameModelInputCapabilities.Text
                    | GameModelInputCapabilities.Image
                    | GameModelInputCapabilities.StructuredData)),
            (nameof(GameModelDescriptor.OutputCapabilities), () => ComparableModel(
                outputCapabilities: GameModelOutputCapabilities.Text
                    | GameModelOutputCapabilities.StructuredData
                    | GameModelOutputCapabilities.ToolCalls
                    | GameModelOutputCapabilities.Reasoning)),
            (nameof(GameModelDescriptor.ReasoningLevels), () => ComparableModel(
                reasoningLevels: new[]
                {
                    GameReasoningLevel.Low,
                    GameReasoningLevel.Medium,
                    GameReasoningLevel.High,
                })),
            (nameof(GameModelDescriptor.ReasoningLevelValues), () => ComparableModel(
                reasoningLevelValues: new Dictionary<GameReasoningLevel, string>
                {
                    [GameReasoningLevel.Low] = "changed-low",
                })),
            ($"{nameof(GameModelDescriptor.Cost)}.Input", () => ComparableModel(cost: ComparableCost(input: 11))),
            ($"{nameof(GameModelDescriptor.Cost)}.Output", () => ComparableModel(cost: ComparableCost(output: 12))),
            ($"{nameof(GameModelDescriptor.Cost)}.CacheRead", () => ComparableModel(cost: ComparableCost(cacheRead: 13))),
            ($"{nameof(GameModelDescriptor.Cost)}.CacheWrite", () => ComparableModel(cost: ComparableCost(cacheWrite: 14))),
            ($"{nameof(GameModelDescriptor.Cost)}.IsKnown", () => ComparableModel(cost: new GameModelCost(isKnown: false))),
            ($"{nameof(GameModelDescriptor.Cost)}.TierCount", () => ComparableModel(cost: new GameModelCost(
                1,
                2,
                3,
                4,
                new[]
                {
                    new GameModelCostTier(50_000, 5, 6, 7, 8),
                    new GameModelCostTier(75_000, 9, 10, 11, 12),
                }))),
            ($"{nameof(GameModelDescriptor.Cost)}.TierThreshold", () => ComparableModel(cost: ComparableCost(tierAbove: 60_000))),
            ($"{nameof(GameModelDescriptor.Cost)}.TierInput", () => ComparableModel(cost: ComparableCost(tierInput: 15))),
            ($"{nameof(GameModelDescriptor.Cost)}.TierOutput", () => ComparableModel(cost: ComparableCost(tierOutput: 16))),
            ($"{nameof(GameModelDescriptor.Cost)}.TierCacheRead", () => ComparableModel(cost: ComparableCost(tierCacheRead: 17))),
            ($"{nameof(GameModelDescriptor.Cost)}.TierCacheWrite", () => ComparableModel(cost: ComparableCost(tierCacheWrite: 18))),
            (nameof(GameModelDescriptor.Metadata), () => ComparableModel(metadata: new Dictionary<string, string>
            {
                ["family"] = "changed",
            })),
            (nameof(GameModelDescriptor.SamplingParametersJson), () => ComparableModel(
                samplingParametersJson: "{\"temperature\":0.5}")),
            (nameof(GameModelDescriptor.Headers), () => ComparableModel(headers: new Dictionary<string, string?>
            {
                ["X-Model-Mode"] = "changed",
            })),
            (nameof(GameModelDescriptor.CompatibilityJson), () => ComparableModel(
                compatibilityJson: "{\"supportsTemperature\":false}")),
        };

        foreach (var change in changes)
        {
            var current = ComparableModel();
            var catalog = Catalog(Registration(
                "provider",
                new ScriptedProvider(),
                Array.Empty<GameModelDescriptor>(),
                refresh: (_, _) => new ValueTask<IReadOnlyList<GameModelDescriptor>>(new[] { current })));

            var first = await catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken);
            var unchanged = await catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken);
            current = change.Create();
            var changed = await catalog.RefreshAsync("provider", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(GameModelRefreshStatus.Updated, first.Status);
            Assert.Equal(GameModelRefreshStatus.Unchanged, unchanged.Status);
            Assert.True(
                changed.Status == GameModelRefreshStatus.Updated,
                $"Changing '{change.Field}' must produce an Updated refresh result, but produced '{changed.Status}'.");
        }
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
    public async Task RefreshStopsWaitingWhenAProviderIgnoresCallerCancellation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = Catalog(Registration(
            "provider",
            new ScriptedProvider(),
            new[] { Model("provider", "baseline") },
            async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return new[] { Model("provider", "late") };
            }));
        using var cancellation = new CancellationTokenSource();
        var refresh = catalog.RefreshAsync("provider", cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        var result = await refresh;

        Assert.Equal(GameModelRefreshStatus.Canceled, result.Status);
        Assert.Equal("baseline", Assert.Single(catalog.GetModels("provider")).ModelId);
        release.TrySetResult(true);
    }

    [Fact]
    public async Task SelectedRefreshIgnoresUnknownProviderIds()
    {
        var refreshes = 0;
        var catalog = Catalog(Registration(
            "known",
            new ScriptedProvider(),
            Array.Empty<GameModelDescriptor>(),
            (_, _) =>
            {
                Interlocked.Increment(ref refreshes);
                return new ValueTask<IReadOnlyList<GameModelDescriptor>>(new[] { Model("known", "model") });
            }));

        var results = await catalog.RefreshAsync(
            new[] { "unknown", "known", "unknown" },
            cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("known", result.ProviderId);
        Assert.Equal(GameModelRefreshStatus.Updated, result.Status);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task AvailabilityStopsWaitingWhenAuthenticationIgnoresCancellation()
    {
        var authentication = new BlockingAuthentication();
        var catalog = Catalog(new GameModelProviderRegistration(
            new GameProviderDescriptor("provider"),
            new ScriptedProvider(),
            authentication,
            new[] { Model("provider", "model") }));
        using var cancellation = new CancellationTokenSource();
        var available = catalog.GetAvailableModelsAsync(
            "provider",
            cancellation.Token).AsTask();
        await authentication.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => available);
        authentication.Release.TrySetResult(true);
    }

    [Fact]
    public async Task CatalogAuthenticationFacadeIsCancellableAndWrapsProviderFailures()
    {
        var blocking = new BlockingAuthentication();
        var catalog = Catalog(new GameModelProviderRegistration(
            new GameProviderDescriptor("blocking"),
            new ScriptedProvider(),
            blocking,
            new[] { Model("blocking", "model") }));
        using var cancellation = new CancellationTokenSource();
        var check = catalog.CheckAuthenticationAsync("blocking", cancellation.Token).AsTask();
        await blocking.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        blocking.Release.TrySetResult(true);

        catalog.Register(new GameModelProviderRegistration(
            new GameProviderDescriptor("throwing"),
            new ScriptedProvider(),
            new ThrowingAuthentication(),
            new[] { Model("throwing", "model") }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CheckAuthenticationAsync("throwing", TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("throwing", error.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(error.InnerException);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            catalog.CheckAuthenticationAsync("missing", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            catalog.ResolveAuthenticationAsync("missing", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            catalog.LoginAsync(
                "missing",
                "api-key",
                new GameAuthInteraction(),
                TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            catalog.LogoutAsync("missing", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task CatalogAuthenticationFacadeRunsLoginResolveAndLogoutThroughOneRegistration()
    {
        var authentication = new RecordingAuthentication();
        var catalog = Catalog(new GameModelProviderRegistration(
            new GameProviderDescriptor("provider"),
            new ScriptedProvider(),
            authentication,
            new[] { Model("provider", "model") }));

        var status = await catalog.CheckAuthenticationAsync("provider", TestContext.Current.CancellationToken);
        var resolution = await catalog.ResolveAuthenticationAsync("provider", TestContext.Current.CancellationToken);
        var credential = await catalog.LoginAsync(
            "provider",
            "api-key",
            new GameAuthInteraction(),
            TestContext.Current.CancellationToken);
        await catalog.LogoutAsync("provider", TestContext.Current.CancellationToken);

        Assert.True(status.Configured);
        Assert.Equal("resolved", resolution!.Credential!.Secret);
        Assert.Equal("logged-in", credential.Secret);
        Assert.Equal(1, authentication.CheckCount);
        Assert.Equal(1, authentication.ResolveCount);
        Assert.Equal(1, authentication.LoginCount);
        Assert.Equal(1, authentication.LogoutCount);
    }

    [Fact]
    public async Task CatalogDeferredFacadeAuthenticatesValidatesIdentityAndForwardsCustomProvider()
    {
        var authentication = new RecordingAuthentication();
        var provider = new DeferredProvider();
        var catalog = Catalog(new GameModelProviderRegistration(
            new GameProviderDescriptor("provider"),
            provider,
            authentication,
            new[] { ComparableModel(api: "deferred-api") }));
        var handle = new DeferredModelHandle("provider", "model", "deferred-api", "job-1");

        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in catalog.FetchDeferredAsync(
                           handle,
                           TimeSpan.FromSeconds(2),
                           TestContext.Current.CancellationToken))
        {
            events.Add(streamEvent);
        }
        await catalog.CancelDeferredAsync(handle, TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events).Kind);
        Assert.Same(handle, provider.FetchedHandle);
        Assert.Same(handle, provider.CanceledHandle);
        Assert.Equal(TimeSpan.FromSeconds(2), provider.Wait);
        Assert.Equal(2, authentication.CheckCount);
        Assert.Equal(2, authentication.ResolveCount);

        var unsupported = Catalog(Registration(
            "plain",
            new ScriptedProvider(),
            ComparableModel(api: "deferred-api", providerId: "plain")));
        var unsupportedError = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var _ in unsupported.FetchDeferredAsync(
                               new DeferredModelHandle("plain", "model", "deferred-api", "job-2"),
                               TimeSpan.Zero,
                               TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.False(unsupportedError.IsTransient);
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
        Assert.All(resolved, value => Assert.Equal("refreshed", value!.Credential!.Secret));
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
    public async Task StoredAuthenticationRefreshesCredentialsInsideTheDefaultFiveMinuteWindow()
    {
        var store = new InMemoryGameCredentialStore();
        var now = DateTimeOffset.UnixEpoch.AddHours(3);
        var key = new GameCredentialKey("provider");
        await store.SetAsync(
            key,
            new GameCredential(GameCredentialKind.OAuth, "expiring", now.AddMinutes(4)),
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
            clock: () => now);

        var resolved = await authentication.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, refreshes);
        Assert.Equal("fresh", resolved!.Credential!.Secret);
        Assert.Equal("fresh", (await store.GetAsync(key, TestContext.Current.CancellationToken))!.Secret);
    }

    [Fact]
    public async Task StoredAuthenticationTimesOutANonCooperativeRefreshWithoutLateCommit()
    {
        var store = new InMemoryGameCredentialStore();
        var now = DateTimeOffset.UnixEpoch.AddHours(3);
        var key = new GameCredentialKey("provider");
        var original = new GameCredential(GameCredentialKind.OAuth, "expired", now.AddMinutes(-1));
        await store.SetAsync(key, original, TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource<GameCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authentication = new StoredGameProviderAuthentication(
            "provider",
            store,
            refresh: (_, _) => new ValueTask<GameCredential>(release.Task),
            clock: () => now,
            refreshSkew: TimeSpan.Zero,
            refreshTimeoutMilliseconds: 100);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await authentication.ResolveAsync(TestContext.Current.CancellationToken));
        Assert.Same(original, await store.GetAsync(key, TestContext.Current.CancellationToken));

        release.TrySetResult(new GameCredential(GameCredentialKind.OAuth, "late", now.AddHours(1)));
        await Task.Yield();
        Assert.Same(original, await store.GetAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CredentialMutationsSerializePerProviderWithoutBlockingOtherProviders()
    {
        var store = new InMemoryGameCredentialStore();
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.ModifyAsync(
            new GameCredentialKey("first"),
            async (_, _) =>
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.ConfigureAwait(false);
                return new GameCredential(GameCredentialKind.ApiKey, "first-secret");
            },
            TestContext.Current.CancellationToken).AsTask();
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var second = await store.ModifyAsync(
            new GameCredentialKey("second"),
            (_, _) => new ValueTask<GameCredential?>(
                new GameCredential(GameCredentialKind.ApiKey, "second-secret")),
            TestContext.Current.CancellationToken);

        Assert.Equal("second-secret", second!.Secret);
        Assert.False(first.IsCompleted);
        releaseFirst.TrySetResult(true);
        Assert.Equal("first-secret", (await first)!.Secret);
    }

    [Fact]
    public async Task CanceledQueuedCredentialMutationNeverRunsLater()
    {
        var store = new InMemoryGameCredentialStore();
        var key = new GameCredentialKey("provider");
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.ModifyAsync(
            key,
            async (_, _) =>
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.ConfigureAwait(false);
                return new GameCredential(GameCredentialKind.ApiKey, "first");
            },
            TestContext.Current.CancellationToken).AsTask();
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        var secondRan = false;
        var second = store.ModifyAsync(
            key,
            (_, _) =>
            {
                secondRan = true;
                return new ValueTask<GameCredential?>(new GameCredential(GameCredentialKind.ApiKey, "second"));
            },
            cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        releaseFirst.TrySetResult(true);
        await first;
        await Task.Yield();
        Assert.False(secondRan);
        Assert.Equal("first", (await store.GetAsync(key, TestContext.Current.CancellationToken))!.Secret);
    }

    [Fact]
    public async Task ActiveCredentialMutationStopsWaitingOnCancellationAndCannotCommitLate()
    {
        var store = new InMemoryGameCredentialStore();
        var key = new GameCredentialKey("provider");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var mutation = store.ModifyAsync(
            key,
            async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return new GameCredential(GameCredentialKind.ApiKey, "late");
            },
            cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        release.TrySetResult(true);

        await store.SetAsync(
            key,
            new GameCredential(GameCredentialKind.ApiKey, "current"),
            TestContext.Current.CancellationToken);
        Assert.Equal("current", (await store.GetAsync(key, TestContext.Current.CancellationToken))!.Secret);
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
    public void AuthenticationResolutionSnapshotsRequestAuthWithoutLeakingMutableState()
    {
        var headers = new Dictionary<string, string?>
        {
            ["Authorization"] = "Bearer token",
            ["X-Suppressed"] = null,
        };
        var configuration = new Dictionary<string, string> { ["ACCOUNT_ID"] = "account" };
        var resolution = new GameProviderAuthResolution(
            credential: null,
            source: "ambient",
            baseUrl: new Uri("https://provider.example/v1"),
            headers,
            configuration);

        headers["Authorization"] = "changed";
        configuration["ACCOUNT_ID"] = "changed";

        Assert.Null(resolution.Credential);
        Assert.Equal("https://provider.example/v1", resolution.BaseUrl!.OriginalString);
        Assert.Equal("Bearer token", resolution.Headers["authorization"]);
        Assert.Null(resolution.Headers["x-suppressed"]);
        Assert.Equal("account", resolution.Configuration["account_id"]);
        Assert.Throws<ArgumentException>(() => new GameProviderAuthResolution(
            null,
            "ambient",
            headers: new Dictionary<string, string?>
            {
                ["Authorization"] = "one",
                ["authorization"] = "two",
            }));
        Assert.Throws<ArgumentException>(() => new GameProviderAuthResolution(
            null,
            "ambient",
            headers: new Dictionary<string, string?> { ["X-Unsafe"] = "value\r\ninjected" }));
        Assert.Throws<ArgumentException>(() => new GameProviderAuthResolution(
            null,
            "ambient",
            baseUrl: new Uri("https://user:secret@provider.example/v1")));
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
    public async Task CancellingLoginWhileCredentialCommitIsQueuedNeverStoresTheCredential()
    {
        var store = new InMemoryGameCredentialStore();
        var key = new GameCredentialKey("provider");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = store.ModifyAsync(
            key,
            async (current, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return current;
            },
            TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var authentication = new StoredGameProviderAuthentication(
            "provider",
            store,
            schemes: new[] { "oauth" },
            login: (_, _, _) => new ValueTask<GameCredential>(
                new GameCredential(GameCredentialKind.OAuth, "must-not-be-stored")));
        using var cancellation = new CancellationTokenSource();
        var login = authentication.LoginAsync(
            "oauth",
            new GameAuthInteraction(),
            cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        release.TrySetResult(true);
        await blocker;
        Assert.Null(await store.GetAsync(key, TestContext.Current.CancellationToken));
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
        Assert.Equal("first", first!.Credential!.Secret);
        Assert.Equal("second", second!.Credential!.Secret);
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
        secrets.Enqueue(authentication?.Credential?.Secret);
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
        capture(authentication?.Credential?.Secret);
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

    private static GameModelDescriptor ComparableModel(
        string modelId = "model",
        string displayName = "Model",
        string api = "openai-completions",
        string baseUrl = "https://example.invalid/v1",
        int contextWindowTokens = 100_000,
        int maximumOutputTokens = 8_000,
        GameModelInputCapabilities inputCapabilities = GameModelInputCapabilities.Text | GameModelInputCapabilities.StructuredData,
        GameModelOutputCapabilities outputCapabilities = GameModelOutputCapabilities.Text
            | GameModelOutputCapabilities.ToolCalls
            | GameModelOutputCapabilities.Reasoning,
        IReadOnlyCollection<GameReasoningLevel>? reasoningLevels = null,
        IReadOnlyDictionary<GameReasoningLevel, string>? reasoningLevelValues = null,
        GameModelCost? cost = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string samplingParametersJson = "{\"temperature\":0.2}",
        IReadOnlyDictionary<string, string?>? headers = null,
        string compatibilityJson = "{\"supportsTemperature\":true}",
        string providerId = "provider") =>
        new(
            providerId,
            modelId,
            displayName,
            contextWindowTokens,
            maximumOutputTokens,
            inputCapabilities,
            outputCapabilities,
            reasoningLevels ?? new[] { GameReasoningLevel.Low, GameReasoningLevel.High },
            cost ?? ComparableCost(),
            metadata ?? new Dictionary<string, string> { ["family"] = "baseline" },
            reasoningLevelValues ?? new Dictionary<GameReasoningLevel, string>
            {
                [GameReasoningLevel.Low] = "baseline-low",
            },
            api,
            new Uri(baseUrl),
            samplingParametersJson,
            headers ?? new Dictionary<string, string?> { ["X-Model-Mode"] = "baseline" },
            compatibilityJson);

    private static GameModelCost ComparableCost(
        decimal input = 1,
        decimal output = 2,
        decimal cacheRead = 3,
        decimal cacheWrite = 4,
        long tierAbove = 50_000,
        decimal tierInput = 5,
        decimal tierOutput = 6,
        decimal tierCacheRead = 7,
        decimal tierCacheWrite = 8) =>
        new(
            input,
            output,
            cacheRead,
            cacheWrite,
            new[]
            {
                new GameModelCostTier(
                    tierAbove,
                    tierInput,
                    tierOutput,
                    tierCacheRead,
                    tierCacheWrite),
            });

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

    private sealed class ThrowingAuthentication : IGameProviderAuthentication
    {
        public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

        public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<GameProviderAuthStatus>(new FormatException("broken auth"));

        public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken) =>
            new((GameProviderAuthResolution?)null);

        public ValueTask<GameCredential> LoginAsync(
            string scheme,
            GameAuthInteraction interaction,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<GameCredential>(new InvalidOperationException());

        public ValueTask LogoutAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingAuthentication : IGameProviderAuthentication
    {
        public IReadOnlyCollection<string> Schemes { get; } = new[] { "api-key" };

        public int CheckCount { get; private set; }

        public int ResolveCount { get; private set; }

        public int LoginCount { get; private set; }

        public int LogoutCount { get; private set; }

        public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCount++;
            return new ValueTask<GameProviderAuthStatus>(new GameProviderAuthStatus(true, "recording"));
        }

        public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCount++;
            return new ValueTask<GameProviderAuthResolution?>(new GameProviderAuthResolution(
                new GameCredential(GameCredentialKind.ApiKey, "resolved"),
                "recording"));
        }

        public ValueTask<GameCredential> LoginAsync(
            string scheme,
            GameAuthInteraction interaction,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoginCount++;
            return new ValueTask<GameCredential>(new GameCredential(GameCredentialKind.ApiKey, "logged-in"));
        }

        public ValueTask LogoutAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogoutCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeferredProvider : IDeferredModelProvider
    {
        public DeferredModelHandle? FetchedHandle { get; private set; }

        public DeferredModelHandle? CanceledHandle { get; private set; }

        public TimeSpan Wait { get; private set; }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ModelStreamEvent> FetchDeferredAsync(
            DeferredModelHandle handle,
            TimeSpan wait,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FetchedHandle = handle;
            Wait = wait;
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("ready") },
                ModelStopReason.Stop));
        }

        public ValueTask CancelDeferredAsync(
            DeferredModelHandle handle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CanceledHandle = handle;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingAuthentication : IGameProviderAuthentication
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

        public async ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Entered.TrySetResult(true);
            await Release.Task.ConfigureAwait(false);
            return new GameProviderAuthStatus(true, "blocking");
        }

        public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameProviderAuthResolution?>((GameProviderAuthResolution?)null);
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
