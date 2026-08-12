using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.Anthropic;
using OpenGameAgent.Providers.Bedrock;
using OpenGameAgent.Providers.Google;
using OpenGameAgent.Providers.Mistral;
using OpenGameAgent.Providers.OpenAI;
using OpenGameAgent.Providers.OpenAICompatible;
using Xunit;

namespace OpenGameAgent.Models.Tests;

public sealed class GameModelDirectoryTests
{
    [Fact]
    public void BundledDirectoryLoadsOfflineWithRichDescriptors()
    {
        var directory = GameModelDirectory.LoadBundled();

        Assert.True(directory.Providers.Count >= 20);
        Assert.True(directory.Models.Count >= 500);
        Assert.NotNull(directory.GetProvider("openai"));
        Assert.NotNull(directory.GetProvider("anthropic"));
        Assert.NotNull(directory.GetProvider("google"));
        Assert.NotNull(directory.GetProvider("deepseek"));

        var reasoningModel = directory.GetModels("openai").First(model =>
                model.OutputCapabilities.HasFlag(GameModelOutputCapabilities.Reasoning)
                && model.InputCapabilities.HasFlag(GameModelInputCapabilities.Image)
                && model.ContextWindowTokens > 0);
        Assert.Contains(GameReasoningLevel.High, reasoningModel.ReasoningLevels);
        Assert.True(reasoningModel.Cost.OutputPerMillionTokens >= 0);
        Assert.NotNull(reasoningModel.CompatibilityJson);
    }

    [Fact]
    public void BundledDirectoryReusesItsImmutableParsedSnapshot()
    {
        var first = GameModelDirectory.LoadBundled();
        var second = GameModelDirectory.LoadBundled();

        Assert.Same(first, second);
    }

    [Fact]
    public void UnknownProviderReturnsAnEmptyList()
    {
        var directory = GameModelDirectory.LoadBundled();

        Assert.Empty(directory.GetModels("not-configured"));
        Assert.Null(directory.GetProvider("not-configured"));
    }

    [Fact]
    public void DirectoryPricingDistinguishesUnavailableKnownFreeAndFreeModelIds()
    {
        const string json = """
            {
              "version": "test",
              "generatedAt": "2026-08-12T00:00:00Z",
              "providers": [{
                "id": "provider",
                "models": [
                  { "id": "unknown", "cost": {} },
                  { "id": "known-free", "cost": { "known": true } },
                  { "id": "model:free", "cost": {} },
                  { "id": "priced", "cost": { "input": 1.25 } }
                ]
              }]
            }
            """;

        var models = GameModelDirectory.ParseJson(json).GetModels("provider")
            .ToDictionary(model => model.ModelId, StringComparer.Ordinal);

        Assert.False(models["unknown"].Cost.IsKnown);
        Assert.True(models["known-free"].Cost.IsKnown);
        Assert.True(models["model:free"].Cost.IsKnown);
        Assert.True(models["priced"].Cost.IsKnown);
    }

    [Fact]
    public void BundledDirectoryApisMatchExecutableProviderCapabilities()
    {
        using var httpClient = new HttpClient();
        var executableProviders = new IModelProviderCapabilities[]
        {
            new AnthropicMessagesProvider(new AnthropicMessagesProviderOptions(
                httpClient,
                new Uri("https://api.anthropic.com/v1/messages"))),
            new BedrockConverseProvider(new BedrockConverseProviderOptions()),
            new GoogleGenerativeProvider(new GoogleGenerativeProviderOptions(
                httpClient,
                new Uri("https://generativelanguage.googleapis.com/v1beta"))),
            new GoogleGenerativeProvider(new GoogleGenerativeProviderOptions(
                httpClient,
                new Uri("https://aiplatform.googleapis.com/v1"),
                GoogleApiFlavor.Vertex)),
            new MistralConversationsProvider(new MistralConversationsProviderOptions(
                httpClient,
                new Uri("https://api.mistral.ai/v1/conversations"))),
            new OpenAIResponsesProvider(new OpenAIResponsesProviderOptions(
                httpClient,
                new Uri("https://api.openai.com/v1/responses"))),
            new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
                httpClient,
                new Uri("https://example.invalid/v1/chat/completions"))),
        };
        var executableApis = executableProviders
            .SelectMany(provider => provider.SupportedApis)
            .ToHashSet(StringComparer.Ordinal);
        var directory = GameModelDirectory.LoadBundled();
        foreach (var model in directory.Models)
        {
            Assert.True(
                executableApis.Contains(model.Api),
                $"Bundled model '{model.ProviderId}/{model.ModelId}' references non-executable API '{model.Api}'.");
        }

        var directoryApis = directory.Models
            .Select(model => model.Api)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            executableApis.OrderBy(api => api, StringComparer.Ordinal),
            directoryApis.OrderBy(api => api, StringComparer.Ordinal));
    }

    [Fact]
    public void BundledVertexDirectoryContainsOnlyGeminiProtocolModels()
    {
        var directory = GameModelDirectory.LoadBundled();

        var models = directory.GetModels("google-vertex");

        Assert.NotEmpty(models);
        Assert.All(models, model => Assert.StartsWith("gemini-", model.ModelId, StringComparison.Ordinal));
        Assert.DoesNotContain(models, model => model.ModelId == "gemini-3.1-flash-lite-preview");
    }

    [Fact]
    public void BundledAnthropicCompatibleProvidersUseTheAnthropicProtocol()
    {
        var directory = GameModelDirectory.LoadBundled();

        foreach (var providerId in new[] { "kimi-for-coding", "minimax", "minimax-cn" })
        {
            var models = directory.GetModels(providerId);
            Assert.NotEmpty(models);
            Assert.All(models, model => Assert.Equal("anthropic-messages", model.Api));
        }
    }

    [Fact]
    public void BundledDirectoryAdvertisesOnlyCapabilitiesOfItsExecutableTextProtocols()
    {
        var directory = GameModelDirectory.LoadBundled();

        Assert.DoesNotContain(directory.Models, model =>
            model.Api == "openai-responses"
            && model.ModelId.Contains("realtime", StringComparison.OrdinalIgnoreCase));
        Assert.All(directory.Models, model =>
        {
            Assert.True(model.InputCapabilities.HasFlag(GameModelInputCapabilities.Text));
            Assert.False(model.InputCapabilities.HasFlag(GameModelInputCapabilities.Audio));
            Assert.False(model.InputCapabilities.HasFlag(GameModelInputCapabilities.Video));
            Assert.True(model.OutputCapabilities.HasFlag(GameModelOutputCapabilities.Text));
            Assert.Equal(
                GameModelOutputCapabilities.None,
                model.OutputCapabilities
                & (GameModelOutputCapabilities.Image
                   | GameModelOutputCapabilities.Audio
                   | GameModelOutputCapabilities.Video));
        });
    }

    [Fact]
    public void BundledDirectoryProvidesExecutableFallbackEndpointsAndDeclaredTemplateInputs()
    {
        var directory = GameModelDirectory.LoadBundled();
        Assert.Equal("api.cerebras.ai", directory.GetProvider("cerebras")!.Endpoint!.Host);
        Assert.Equal("api.groq.com", directory.GetProvider("groq")!.Endpoint!.Host);
        Assert.Equal("api.together.ai", directory.GetProvider("togetherai")!.Endpoint!.Host);
        Assert.Equal("api.x.ai", directory.GetProvider("xai")!.Endpoint!.Host);

        foreach (var provider in directory.Providers)
        {
            var endpoint = provider.Endpoint?.OriginalString;
            if (endpoint is null || !endpoint.Contains("${", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                provider.Metadata.TryGetValue("environmentVariables", out var declared),
                $"Provider '{provider.ProviderId}' has a templated endpoint without declared configuration variables.");
            var variables = declared!.Split(',').Select(value => value.Trim()).ToHashSet(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(endpoint, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}"))
            {
                Assert.Contains(match.Groups[1].Value, variables);
            }

            Assert.DoesNotContain("${", System.Text.RegularExpressions.Regex.Replace(
                endpoint,
                @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
                string.Empty),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BundledReasoningProfilesPreserveProviderVocabularyAndAlwaysThinkingModels()
    {
        var directory = GameModelDirectory.LoadBundled();

        var openAi = directory.GetModels("openai").Single(model => model.ModelId == "gpt-5.4");
        Assert.Equal("none", openAi.GetReasoningValue(GameReasoningLevel.Off));
        Assert.Equal("xhigh", openAi.GetReasoningValue(GameReasoningLevel.ExtraHigh));
        Assert.DoesNotContain(GameReasoningLevel.Maximum, openAi.ReasoningLevels);
        var longContextTier = Assert.Single(openAi.Cost.Tiers);
        Assert.Equal(272_000, longContextTier.InputTokensAbove);
        Assert.Equal(5m, longContextTier.InputPerMillionTokens);
        Assert.Equal(22.5m, longContextTier.OutputPerMillionTokens);
        Assert.Equal(openAi.Cost.InputPerMillionTokens, openAi.Cost.RatesForInput(272_000).InputPerMillionTokens);
        Assert.Equal(longContextTier.InputPerMillionTokens, openAi.Cost.RatesForInput(272_001).InputPerMillionTokens);

        var google = directory.GetModels("google").Single(model => model.ModelId == "gemini-3.1-pro-preview");
        Assert.Equal(new[] { GameReasoningLevel.Low, GameReasoningLevel.High }, google.ReasoningLevels);
        Assert.Equal("LOW", google.GetReasoningValue(GameReasoningLevel.Low));
        Assert.Equal("HIGH", google.GetReasoningValue(GameReasoningLevel.High));

        var alwaysThinking = directory.GetModels("moonshotai")
            .Single(model => model.ModelId == "kimi-k2.7-code");
        Assert.DoesNotContain(GameReasoningLevel.Off, alwaysThinking.ReasoningLevels);
        Assert.Equal(GameReasoningLevel.Minimal, alwaysThinking.ClampReasoning(GameReasoningLevel.Off));

        var zai = directory.GetModels("zai").Single(model => model.ModelId == "glm-5.2");
        Assert.Contains(GameReasoningLevel.Off, zai.ReasoningLevels);
        Assert.Null(zai.GetReasoningValue(GameReasoningLevel.Off));
        Assert.Equal("high", zai.GetReasoningValue(GameReasoningLevel.Low));
        Assert.Equal("max", zai.GetReasoningValue(GameReasoningLevel.Maximum));

        var fireworks = directory.GetModels("fireworks-ai")
            .Single(model => model.ModelId == "accounts/fireworks/models/glm-5p2");
        Assert.Equal("none", fireworks.GetReasoningValue(GameReasoningLevel.Off));
        Assert.Equal("high", fireworks.GetReasoningValue(GameReasoningLevel.Medium));

        var fable = directory.GetModels("anthropic").Single(model => model.ModelId == "claude-fable-5");
        Assert.DoesNotContain(GameReasoningLevel.Off, fable.ReasoningLevels);
        Assert.Contains(GameReasoningLevel.ExtraHigh, fable.ReasoningLevels);
        Assert.Contains(GameReasoningLevel.Maximum, fable.ReasoningLevels);
    }

    [Fact]
    public void BundledCompatibilityDeltasCoverOpenAiCompatibleAndNativeProtocolFamilies()
    {
        var directory = GameModelDirectory.LoadBundled();

        AssertCompatibility("zai", "glm-5.2", "thinkingFormat", "zai");
        AssertCompatibility("deepseek", "deepseek-v4-flash", "requiresReasoningContentOnAssistantMessages", true);
        AssertCompatibility("moonshotai", "kimi-k2.5", "maxTokensField", "max_tokens");
        AssertCompatibility("togetherai", "deepseek-ai/DeepSeek-V4-Pro", "thinkingFormat", "together");
        AssertCompatibility("nvidia", "minimaxai/minimax-m3", "supportsStrictMode", false);
        AssertCompatibility("cloudflare-workers-ai", "@cf/openai/gpt-oss-20b", "sendSessionAffinityHeaders", true);
        AssertCompatibility("openrouter", "openai/gpt-5.4", "thinkingFormat", "openrouter");
        AssertCompatibility("xai", "grok-4.5", "supportsLongCacheRetention", false);
        AssertCompatibility("openai", "gpt-5.6-sol", "supportsExplicitPromptCacheMode", true);
        AssertCompatibility("anthropic", "claude-opus-4-6", "forceAdaptiveThinking", true);

        var fireworksOpenAi = directory.GetModels("fireworks-ai")
            .Single(model => model.ModelId == "accounts/fireworks/models/glm-5p2");
        var fireworksAnthropic = directory.GetModels("fireworks-ai")
            .First(model => model.Api == "anthropic-messages");
        Assert.Equal("openai-completions", fireworksOpenAi.Api);
        Assert.Equal("https://api.fireworks.ai/inference/v1", fireworksOpenAi.BaseUrl!.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("https://api.fireworks.ai/inference", fireworksAnthropic.BaseUrl!.AbsoluteUri.TrimEnd('/'));

        void AssertCompatibility(string provider, string modelId, string property, object expected)
        {
            var model = directory.GetModels(provider).Single(value => value.ModelId == modelId);
            using var document = JsonDocument.Parse(model.CompatibilityJson!);
            var value = document.RootElement.GetProperty(property);
            if (expected is bool boolean)
            {
                Assert.Equal(boolean, value.GetBoolean());
            }
            else
            {
                Assert.Equal((string)expected, value.GetString());
            }
        }
    }

    [Fact]
    public void ParserRejectsDuplicateProviders()
    {
        const string json = """
            {
              "version": "1",
              "generatedAt": "2026-01-01T00:00:00Z",
              "providers": [
                { "id": "same", "name": "One", "models": [] },
                { "id": "same", "name": "Two", "models": [] }
              ]
            }
            """;

        var error = Assert.Throws<ArgumentException>(() => GameModelDirectory.ParseJson(json));
        Assert.Contains("duplicate provider", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParserPreservesCapabilitiesReasoningCostsAndCompatibility()
    {
        const string json = """
            {
              "version": "1",
              "generatedAt": "2026-01-01T00:00:00Z",
              "providers": [{
                "id": "local",
                "name": "Local",
                "endpoint": "http://127.0.0.1:1234/v1",
                "local": true,
                "models": [{
                  "id": "model",
                  "name": "Model",
                  "api": "openai-completions",
                  "contextWindow": 32000,
                  "maximumOutput": 4096,
                  "input": ["text", "image", "structured"],
                  "output": ["text", "structured", "tools", "reasoning"],
                  "reasoning": ["off", "low", "high"],
                  "reasoningValues": { "off": "none", "low": "small", "high": "large" },
                  "cost": { "input": 1.25, "output": 5.0, "cacheRead": 0.1, "cacheWrite": 0.2 },
                  "headers": { "X-Kept": "value", "X-Suppressed": null },
                  "compatibility": { "supportsStrictMode": true }
                }]
              }]
            }
            """;

        var directory = GameModelDirectory.ParseJson(json);
        var provider = Assert.Single(directory.Providers);
        var model = Assert.Single(directory.Models);

        Assert.True(provider.IsLocal);
        Assert.Equal("http://127.0.0.1:1234/v1", provider.Endpoint!.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("openai-completions", model.Api);
        Assert.Equal(GameModelInputCapabilities.Text | GameModelInputCapabilities.Image | GameModelInputCapabilities.StructuredData, model.InputCapabilities);
        Assert.True(model.OutputCapabilities.HasFlag(GameModelOutputCapabilities.ToolCalls));
        Assert.Equal("none", model.GetReasoningValue(GameReasoningLevel.Off));
        Assert.Equal("small", model.GetReasoningValue(GameReasoningLevel.Low));
        Assert.Equal(5m, model.Cost.OutputPerMillionTokens);
        Assert.Equal("value", model.Headers["x-kept"]);
        Assert.Null(model.Headers["x-suppressed"]);
        Assert.Contains("supportsStrictMode", model.CompatibilityJson, StringComparison.Ordinal);
    }
}
