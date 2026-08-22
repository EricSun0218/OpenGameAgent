using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime.Model;
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.Bedrock;
using OpenGameAgent.ProviderTransport;
using Xunit;

namespace OpenGameAgent.Models.BuiltIn.Tests;

public sealed class BuiltInGameModelRuntimeTests
{
    public static TheoryData<string, string, string, string, string> HttpApis => new()
    {
        {
            BuiltInGameModelApis.OpenAiResponses,
            "openai",
            "https://catalog.invalid/openai/v1",
            "/custom/responses",
            "authorization"
        },
        {
            BuiltInGameModelApis.OpenAiCompletions,
            "compatible",
            "https://catalog.invalid/compatible/v1",
            "/compatible/v1/chat/completions",
            "x-model-token"
        },
        {
            BuiltInGameModelApis.AnthropicMessages,
            "anthropic",
            "https://catalog.invalid/anthropic/v1",
            "/anthropic/v1/messages",
            "x-api-key"
        },
        {
            BuiltInGameModelApis.GoogleGenerativeAi,
            "google",
            "https://catalog.invalid/google/v1beta",
            "/google/v1beta/models/model:streamGenerateContent",
            "x-goog-api-key"
        },
        {
            BuiltInGameModelApis.GoogleVertex,
            "google-vertex",
            "https://catalog.invalid/vertex/v1/projects/p/locations/l/publishers/google",
            "/vertex/v1/projects/p/locations/l/publishers/google/models/model:streamGenerateContent",
            "authorization"
        },
        {
            BuiltInGameModelApis.MistralConversations,
            "mistral",
            "https://catalog.invalid/mistral/v1",
            "/mistral/v1/chat/completions",
            "authorization"
        },
    };

    public static TheoryData<string, string, string, string, string, bool, bool, bool> OpenAiCompatibleFamilies => new()
    {
        { "zai", "glm-5.2", "max_tokens", "system", "zai", false, true, false },
        { "deepseek", "deepseek-v4-flash", "max_tokens", "system", "deepseek", false, true, false },
        { "deepseek", "deepseek-v4-pro", "max_tokens", "system", "deepseek", false, true, false },
        { "moonshotai", "kimi-k2.5", "max_tokens", "system", "deepseek-toggle", false, true, false },
        { "togetherai", "deepseek-ai/DeepSeek-V4-Pro", "max_tokens", "system", "together", false, false, false },
        { "nvidia", "minimaxai/minimax-m3", "max_tokens", "system", "none", false, false, false },
        { "cloudflare-workers-ai", "@cf/openai/gpt-oss-20b", "max_completion_tokens", "system", "effort", false, false, true },
        { "openrouter", "openai/gpt-5.4", "max_completion_tokens", "developer", "openrouter", true, true, false },
        { "fireworks-ai", "accounts/fireworks/models/glm-5p2", "max_completion_tokens", "system", "effort", false, false, true },
        { "baseten", "zai-org/GLM-5.2", "max_tokens", "system", "baseten", false, false, false },
    };

    [Fact]
    public void BundledDirectoryRegistersEveryProviderIntoTheSharedCatalog()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = _ => null,
        };

        var runtime = new BuiltInGameModelRuntime(options);

        Assert.Equal(runtime.Directory.Providers.Count, runtime.Catalog.GetProviders().Count);
        Assert.Equal(runtime.Directory.Models.Count, runtime.Catalog.GetModels().Count);
        var bundledApis = runtime.Catalog.GetModels()
            .Select(model => model.Api)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.All(bundledApis, api => Assert.Contains(api, BuiltInGameModelRuntime.SupportedApis));
        Assert.Contains(BuiltInGameModelApis.AzureOpenAiResponses, BuiltInGameModelRuntime.SupportedApis);
        Assert.Contains(BuiltInGameModelApis.OpenAiCodexResponses, BuiltInGameModelRuntime.SupportedApis);
        Assert.DoesNotContain(BuiltInGameModelApis.AzureOpenAiResponses, bundledApis);
        Assert.DoesNotContain(BuiltInGameModelApis.OpenAiCodexResponses, bundledApis);
        Assert.Equal(9, BuiltInGameModelRuntime.SupportedApis.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AzureDirectoryAuthenticationAndConfigurationReachTheResponsesWire()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.AzureOpenAiResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory(
                "azure-custom",
                BuiltInGameModelApis.AzureOpenAiResponses,
                "https://directory.openai.azure.com/openai",
                modelId: "logical-model"));
        ProviderResponseObservation? observed = null;
        options.ResponseObserver = (value, _) =>
        {
            observed = value;
            return default;
        };
        options.Authentications.Add(
            "azure-custom",
            new FixedAuthentication(new GameProviderAuthResolution(
                new GameCredential(GameCredentialKind.ApiKey, "azure-secret"),
                "resolved",
                new Uri("https://resolved.openai.azure.com/openai"),
                new Dictionary<string, string?> { ["X-Resolved"] = "yes" },
                new Dictionary<string, string>
                {
                    [BuiltInGameModelConfigurationKeys.AzureApiVersion] = "2025-04-01-preview",
                    [BuiltInGameModelConfigurationKeys.AzureDeploymentName] = "deployment-one",
                })));
        var runtime = new BuiltInGameModelRuntime(options);

        var descriptor = runtime.Catalog.GetModel("azure-custom", "logical-model");
        Assert.NotNull(descriptor);
        Assert.Equal(BuiltInGameModelApis.AzureOpenAiResponses, descriptor!.Api);
        var events = await CollectAsync(runtime.StreamAsync(
            "azure-custom",
            Request(model: "logical-model"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal("resolved.openai.azure.com", handler.RequestUri!.Host);
        Assert.Equal("/openai/v1/responses", handler.RequestUri.AbsolutePath);
        Assert.Contains("api-version=2025-04-01-preview", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("azure-secret", handler.Header("api-key"));
        Assert.Equal("yes", handler.Header("X-Resolved"));
        Assert.Contains("\"model\":\"deployment-one\"", handler.Body, StringComparison.Ordinal);
        Assert.Equal(BuiltInGameModelApis.AzureOpenAiResponses, observed!.ApiId);
    }

    [Fact]
    public async Task AzureDefaultEnvironmentMapsKeyEndpointVersionAndDeployment()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.AzureOpenAiResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory(
                "azure-env",
                BuiltInGameModelApis.AzureOpenAiResponses,
                "https://directory.openai.azure.com/openai",
                modelId: "logical-model"));
        options.GetEnvironmentVariable = name => name switch
        {
            "AZURE_OPENAI_API_KEY" => "environment-secret",
            "AZURE_OPENAI_BASE_URL" => "https://environment.openai.azure.com/openai",
            "AZURE_OPENAI_API_VERSION" => "2026-01-01-preview",
            "AZURE_OPENAI_DEPLOYMENT_NAME" => "environment-deployment",
            _ => null,
        };
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "azure-env",
            Request(model: "logical-model"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal("environment.openai.azure.com", handler.RequestUri!.Host);
        Assert.Contains("api-version=2026-01-01-preview", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("environment-secret", handler.Header("api-key"));
        Assert.Contains("\"model\":\"environment-deployment\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAzureKeyIsAnInBandFailure()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.AzureOpenAiResponses);
        using var client = new HttpClient(handler);
        var runtime = new BuiltInGameModelRuntime(Options(
            client,
            Directory(
                "azure-missing",
                BuiltInGameModelApis.AzureOpenAiResponses,
                "https://missing.openai.azure.com/openai")));

        var events = await CollectAsync(runtime.StreamAsync(
            "azure-missing",
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("credential", failure.Response!.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData(BuiltInGameModelConfigurationKeys.AzureApiVersion, "bad version", "API version")]
    [InlineData(BuiltInGameModelConfigurationKeys.AzureDeploymentName, "bad deployment", "deployment")]
    public async Task InvalidAzureRequestConfigurationIsAnInBandFailure(
        string key,
        string value,
        string expectedError)
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.AzureOpenAiResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory(
                "azure-invalid",
                BuiltInGameModelApis.AzureOpenAiResponses,
                "https://invalid.openai.azure.com/openai"));
        options.Authentications.Add(
            "azure-invalid",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "azure-secret")));
        var configuration = new GameModelProviderTransportConfiguration();
        configuration.Options[key] = value;
        options.ProviderConfigurations.Add("azure-invalid", configuration);
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "azure-invalid",
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains(expectedError, failure.Response!.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task CodexOAuthResolutionAndAccountConfigurationReachTheResponsesWire()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiCodexResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory(
                "codex-custom",
                BuiltInGameModelApis.OpenAiCodexResponses,
                "https://directory.invalid/backend-api/codex",
                modelId: "gpt-codex"));
        ProviderResponseObservation? observed = null;
        options.ResponseObserver = (value, _) =>
        {
            observed = value;
            return default;
        };
        options.Authentications.Add(
            "codex-custom",
            new FixedAuthentication(new GameProviderAuthResolution(
                new GameCredential(GameCredentialKind.OAuth, CodexToken("embedded-account")),
                "oauth",
                new Uri("https://resolved.invalid/backend-api/codex"),
                new Dictionary<string, string?> { ["X-Resolved"] = "yes" },
                new Dictionary<string, string>
                {
                    [BuiltInGameModelConfigurationKeys.OpenAiCodexAccountId] = "configured-account",
                })));
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "codex-custom",
            Request(model: "gpt-codex"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal("resolved.invalid", handler.RequestUri!.Host);
        Assert.Equal("/backend-api/codex/responses", handler.RequestUri.AbsolutePath);
        Assert.Equal("Bearer " + CodexToken("embedded-account"), handler.Header("Authorization"));
        Assert.Equal("configured-account", handler.Header("chatgpt-account-id"));
        Assert.Equal("yes", handler.Header("X-Resolved"));
        Assert.Equal("opengameagent", handler.Header("originator"));
        Assert.Contains("\"model\":\"gpt-codex\"", handler.Body, StringComparison.Ordinal);
        Assert.Equal(BuiltInGameModelApis.OpenAiCodexResponses, observed!.ApiId);
    }

    [Fact]
    public async Task CodexRejectsApiKeysAsAnInBandFailure()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiCodexResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory(
                "codex-key",
                BuiltInGameModelApis.OpenAiCodexResponses,
                "https://codex.invalid/backend-api/codex"));
        options.Authentications.Add(
            "codex-key",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, CodexToken("account"))));
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "codex-key",
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("API keys are not accepted", failure.Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task CodexEnvironmentCredentialRequiresExplicitOptIn()
    {
        var token = CodexToken("environment-account");
        var implicitHandler = new RecordingHandler(BuiltInGameModelApis.OpenAiCodexResponses);
        using var implicitClient = new HttpClient(implicitHandler);
        var implicitReads = new List<string>();
        var implicitOptions = Options(
            implicitClient,
            Directory(
                "codex-environment",
                BuiltInGameModelApis.OpenAiCodexResponses,
                "https://codex.invalid/backend-api/codex",
                environmentVariables: "OPENAI_API_KEY,CODEX_ACCESS_TOKEN"));
        implicitOptions.GetEnvironmentVariable = name =>
        {
            implicitReads.Add(name);
            return name is "OPENAI_API_KEY" or "CODEX_ACCESS_TOKEN" ? token : null;
        };
        var implicitRuntime = new BuiltInGameModelRuntime(implicitOptions);

        var implicitEvents = await CollectAsync(implicitRuntime.StreamAsync(
            "codex-environment",
            Request(),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(implicitEvents, item => item.IsTerminal).Kind);
        Assert.Empty(implicitReads);
        Assert.Null(implicitHandler.RequestUri);

        var explicitHandler = new RecordingHandler(BuiltInGameModelApis.OpenAiCodexResponses);
        using var explicitClient = new HttpClient(explicitHandler);
        var explicitOptions = Options(
            explicitClient,
            Directory(
                "codex-environment",
                BuiltInGameModelApis.OpenAiCodexResponses,
                "https://codex.invalid/backend-api/codex"));
        explicitOptions.GetEnvironmentVariable = name => name == "CODEX_ACCESS_TOKEN" ? token : null;
        var configuration = new GameModelProviderTransportConfiguration();
        configuration.Options[BuiltInGameModelConfigurationKeys.OpenAiCodexEnvironmentVariable] =
            "CODEX_ACCESS_TOKEN";
        explicitOptions.ProviderConfigurations.Add("codex-environment", configuration);
        var explicitRuntime = new BuiltInGameModelRuntime(explicitOptions);

        var explicitEvents = await CollectAsync(explicitRuntime.StreamAsync(
            "codex-environment",
            Request(),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(explicitEvents, item => item.IsTerminal).Kind);
        Assert.Equal("Bearer " + token, explicitHandler.Header("Authorization"));
    }

    [Fact]
    public void CatalogDescriptorsExposeOnlyMediaCapabilitiesImplementedByTextProviders()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var runtime = new BuiltInGameModelRuntime(new BuiltInGameModelRuntimeOptions(client)
        {
            Directory = MediaCapabilityDirectory(),
            GetEnvironmentVariable = _ => null,
        });
        var rawInput = Assert.Single(runtime.Directory.Models);
        var executableInput = runtime.Catalog.GetModel(rawInput.ProviderId, rawInput.ModelId)!;
        Assert.False(executableInput.InputCapabilities.HasFlag(GameModelInputCapabilities.Audio));
        Assert.False(executableInput.InputCapabilities.HasFlag(GameModelInputCapabilities.Video));
        Assert.Throws<InvalidOperationException>(() => runtime.Catalog.Resolve(
            rawInput.ProviderId,
            rawInput.ModelId,
            requiredInput: GameModelInputCapabilities.Audio));

        Assert.Equal(
            GameModelOutputCapabilities.None,
            executableInput.OutputCapabilities
            & (GameModelOutputCapabilities.Image
               | GameModelOutputCapabilities.Audio
               | GameModelOutputCapabilities.Video));
        Assert.All(runtime.Catalog.GetModels(), model =>
        {
            Assert.Equal(
                GameModelInputCapabilities.None,
                model.InputCapabilities
                & ~(GameModelInputCapabilities.Text
                    | GameModelInputCapabilities.Image
                    | GameModelInputCapabilities.StructuredData));
            Assert.Equal(
                GameModelOutputCapabilities.None,
                model.OutputCapabilities
                & ~(GameModelOutputCapabilities.Text
                    | GameModelOutputCapabilities.StructuredData
                    | GameModelOutputCapabilities.ToolCalls
                    | GameModelOutputCapabilities.Reasoning));
        });
    }

    [Fact]
    public async Task BundledModelRejectsUnsupportedAudioBeforeGoogleWireSerialization()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.GoogleGenerativeAi);
        using var client = new HttpClient(handler);
        var options = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = name => name == "GOOGLE_API_KEY" ? "google-key" : null,
        };
        var runtime = new BuiltInGameModelRuntime(options);
        var raw = runtime.Directory.GetModels("google").First();
        var request = new ModelRequest(
            raw.ModelId,
            "system",
            new[]
            {
                new AgentMessage(
                    AgentRole.User,
                    new AgentContent[]
                    {
                        new TextContent("listen"),
                        new BinaryContent(AgentMediaKind.Audio, "YXVkaW8=", "audio/wav"),
                    },
                    DateTimeOffset.UnixEpoch),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            "session",
            "run",
            1);

        var events = await CollectAsync(runtime.StreamAsync(
            "google",
            request,
            TestContext.Current.CancellationToken));

        var terminal = Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(ModelStreamEventKind.Failed, terminal.Kind);
        Assert.Contains("does not declare audio input support", terminal.Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(handler.Body);
        Assert.DoesNotContain("YXVkaW8=", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixedApiProviderBuildsOneProviderScopedEnvironmentCredentialChain()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(client, MixedApiDirectory());
        options.GetEnvironmentVariable = name => name == "OPENAI_API_KEY" ? "mixed-key" : null;

        var runtime = new BuiltInGameModelRuntime(options);

        var available = await runtime.Catalog.GetAvailableModelsAsync(
            "mixed",
            TestContext.Current.CancellationToken);
        Assert.Equal(2, available.Count);
        var authentication = runtime.Catalog.GetProvider("mixed")!.Authentication;
        var resolution = await authentication.ResolveAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(resolution);
        Assert.Equal(GameCredentialKind.ApiKey, resolution!.Credential!.Kind);
        Assert.Equal("mixed-key", resolution.Credential.Secret);
    }

    [Fact]
    public async Task DirectoryDeclaredNonstandardEnvironmentCredentialPrecedesApiFallback()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiCompletions);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory(
                "huggingface",
                BuiltInGameModelApis.OpenAiCompletions,
                "https://huggingface.invalid/v1",
                "HF_TOKEN,HF_TOKEN,HUGGINGFACE_API_KEY"));
        var reads = new List<string>();
        options.GetEnvironmentVariable = name =>
        {
            reads.Add(name);
            return name == "HF_TOKEN" ? "hf-secret" : null;
        };
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "huggingface",
            Request(),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal("Bearer hf-secret", handler.Header("Authorization"));
        Assert.Equal(2, reads.Count);
        Assert.All(reads, name => Assert.Equal("HF_TOKEN", name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectoryPricingIsAppliedWithoutTurningUnknownPriceIntoFree(bool pricingKnown)
    {
        const string response = """
            data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":4,"prompt_tokens_details":{"cached_tokens":3,"cache_write_tokens":2},"completion_tokens_details":{"reasoning_tokens":1}}}

            data: {"id":"response-1","model":"served-model","choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiCompletions, responseBody: response);
        using var client = new HttpClient(handler);
        var cost = pricingKnown
            ? new Dictionary<string, object?>
            {
                ["known"] = true,
                ["input"] = 1m,
                ["output"] = 2m,
                ["cacheRead"] = 0.5m,
                ["cacheWrite"] = 1.5m,
            }
            : new Dictionary<string, object?> { ["known"] = false };
        var options = Options(client, Directory(
            "compatible",
            BuiltInGameModelApis.OpenAiCompletions,
            "https://compatible.invalid/v1",
            cost: cost));
        options.Authentications.Add(
            "compatible",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "secret")));
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "compatible",
            Request(),
            TestContext.Current.CancellationToken));

        var usage = Assert.Single(events, item => item.IsTerminal).Response!.Usage;
        Assert.Equal(5, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(3, usage.CacheReadTokens);
        Assert.Equal(2, usage.CacheWriteTokens);
        Assert.Equal(1, usage.ReasoningTokens);
        Assert.Equal(pricingKnown, usage.Cost.IsKnown);
        if (pricingKnown)
        {
            Assert.Equal(0.000005, usage.Cost.Input, 10);
            Assert.Equal(0.000008, usage.Cost.Output, 10);
            Assert.Equal(0.0000015, usage.Cost.CacheRead, 10);
            Assert.Equal(0.000003, usage.Cost.CacheWrite, 10);
        }
        else
        {
            Assert.Null(usage.Cost.TotalIfKnown);
        }
    }

    [Fact]
    public void InvalidDirectoryEnvironmentVariableMetadataIsRejected()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiCompletions));
        var options = Options(
            client,
            Directory(
                "compatible",
                BuiltInGameModelApis.OpenAiCompletions,
                "https://compatible.invalid/v1",
                "VALID_API_KEY,BAD-NAME"));

        var error = Assert.Throws<ArgumentException>(() => new BuiltInGameModelRuntime(options));

        Assert.Contains("environment variable metadata", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(HttpApis))]
    public async Task DirectoryConfigurationAndApiDispatchReachEachHttpProvider(
        string api,
        string providerId,
        string directoryEndpoint,
        string expectedPath,
        string authenticationHeader)
    {
        var handler = new RecordingHandler(api);
        using var client = new HttpClient(handler);
        var options = Options(client, Directory(providerId, api, directoryEndpoint));
        ProviderResponseObservation? observed = null;
        options.ResponseObserver = (value, _) =>
        {
            observed = value;
            return default;
        };
        var credentialKind = api == BuiltInGameModelApis.GoogleVertex
            ? GameCredentialKind.BearerToken
            : GameCredentialKind.ApiKey;
        var authentication = new StaticGameProviderAuthentication(
            credential: new GameCredential(credentialKind, "test-credential"));
        options.Authentications.Add(providerId, authentication);
        var configuration = new GameModelProviderTransportConfiguration();
        configuration.Headers["X-Configuration"] = "runtime";
        if (api == BuiltInGameModelApis.OpenAiResponses)
        {
            configuration.BaseUrl = new Uri("https://override.invalid/custom");
        }

        if (api == BuiltInGameModelApis.OpenAiCompletions)
        {
            configuration.Options[BuiltInGameModelConfigurationKeys.AuthenticationHeader] = "X-Model-Token";
            configuration.Options[BuiltInGameModelConfigurationKeys.AuthenticationScheme] = "Token";
        }

        options.ProviderConfigurations.Add(providerId, configuration);
        var runtime = new BuiltInGameModelRuntime(options);
        var registration = runtime.Catalog.GetProvider(providerId);

        Assert.NotNull(registration);
        Assert.Same(authentication, registration!.Authentication);
        var events = await CollectAsync(runtime.Catalog.Resolve(providerId, "model").Provider.StreamAsync(
            Request(temperature: 0.75, maxOutputTokens: 4096),
            TestContext.Current.CancellationToken));

        var terminal = Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(ModelStreamEventKind.Completed, terminal.Kind);
        Assert.NotNull(handler.RequestUri);
        Assert.Equal(expectedPath, handler.RequestUri!.AbsolutePath);
        if (api == BuiltInGameModelApis.OpenAiResponses)
        {
            Assert.Equal("override.invalid", handler.RequestUri.Host);
        }
        else
        {
            Assert.Equal("catalog.invalid", handler.RequestUri.Host);
        }

        Assert.Equal("catalog", handler.Header("X-Directory"));
        Assert.Equal("runtime", handler.Header("X-Configuration"));
        Assert.NotNull(handler.Header(authenticationHeader));
        Assert.Contains("test-credential", handler.Header(authenticationHeader), StringComparison.Ordinal);
        Assert.NotNull(observed);
        Assert.Equal(api, observed!.ApiId);
        Assert.Equal(providerId, observed.ProviderId);
        if (api == BuiltInGameModelApis.OpenAiCompletions)
        {
            Assert.Equal("Token test-credential", handler.Header(authenticationHeader));
        }

        Assert.Contains("\"directory_marker\":\"applied\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"temperature\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("512", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VertexApplicationDefaultCredentialIsResolvedLazilyInsideTheProviderRequest()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.GoogleVertex);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory("google-vertex", BuiltInGameModelApis.GoogleVertex, "https://vertex.invalid/v1"));
        options.GetEnvironmentVariable = name => name switch
        {
            "GOOGLE_CLOUD_PROJECT" => "project",
            "GOOGLE_CLOUD_LOCATION" => "location",
            _ => null,
        };
        var calls = 0;
        options.VertexApplicationDefaultCredential = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls++;
            return new ValueTask<string?>("adc-token");
        };
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "google-vertex",
            Request(),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal(1, calls);
        Assert.Equal("Bearer adc-token", handler.Header("Authorization"));
    }

    [Fact]
    public async Task VertexExplicitApiKeyUsesGoogleApiKeyAuthentication()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.GoogleVertex);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory("google-vertex", BuiltInGameModelApis.GoogleVertex, "https://vertex.invalid/v1"));
        options.Authentications.Add(
            "google-vertex",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "vertex-api-key")));
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "google-vertex",
            Request(),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal("vertex-api-key", handler.Header("x-goog-api-key"));
        Assert.Null(handler.Header("Authorization"));
    }

    [Theory]
    [InlineData("http://remote.invalid/v1", false, false)]
    [InlineData("http://remote.invalid/v1", true, true)]
    [InlineData("http://127.0.0.1:12345/v1", false, true)]
    public async Task InsecureHttpRequiresExplicitOptInExceptForLoopback(
        string endpoint,
        bool allowInsecureHttp,
        bool shouldComplete)
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, endpoint));
        options.AllowInsecureHttp = allowInsecureHttp;
        options.Authentications.Add(
            "openai",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "key")));
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(),
            TestContext.Current.CancellationToken));

        var terminal = Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(shouldComplete ? ModelStreamEventKind.Completed : ModelStreamEventKind.Failed, terminal.Kind);
        if (shouldComplete)
        {
            Assert.Equal(Uri.UriSchemeHttp, handler.RequestUri!.Scheme);
        }
        else
        {
            Assert.Contains("HTTPS", terminal.Response!.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Null(handler.RequestUri);
        }
    }

    [Fact]
    public async Task BundledCloudflareGatewayExpandsEnvironmentEndpointAndUsesGatewayAuthHeader()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiCompletions);
        using var client = new HttpClient(handler);
        var options = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = name => name switch
            {
                "CLOUDFLARE_API_TOKEN" => "cloudflare-token",
                "CLOUDFLARE_ACCOUNT_ID" => "account",
                "CLOUDFLARE_GATEWAY_ID" => "gateway",
                _ => null,
            },
        };
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "cloudflare-ai-gateway",
            Request(model: "anthropic/claude-3-5-haiku"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal("gateway.ai.cloudflare.com", handler.RequestUri!.Host);
        Assert.Equal("/v1/account/gateway/compat/chat/completions", handler.RequestUri.AbsolutePath);
        Assert.Equal("Bearer cloudflare-token", handler.Header("cf-aig-authorization"));
        Assert.Null(handler.Header("Authorization"));
    }

    [Fact]
    public async Task DirectoryConfigurationAndApiDispatchReachBedrockProvider()
    {
        ConverseStreamRequest? captured = null;
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory(
                "amazon-bedrock",
                BuiltInGameModelApis.BedrockConverseStream,
                "https://bedrock.invalid"));
        var configuration = new GameModelProviderTransportConfiguration
        {
            BedrockTransport = (request, cancellationToken) => Capture(request, cancellationToken),
        };
        configuration.Options[BuiltInGameModelConfigurationKeys.AwsRegion] = "us-east-1";
        options.ProviderConfigurations.Add("amazon-bedrock", configuration);
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "amazon-bedrock",
            Request(temperature: 0.75, maxOutputTokens: 4096),
            TestContext.Current.CancellationToken));

        var terminal = Assert.Single(events, item => item.IsTerminal);
        Assert.True(
            terminal.Kind == ModelStreamEventKind.Completed,
            terminal.Response?.ErrorMessage ?? "The provider did not complete.");
        Assert.NotNull(captured);
        Assert.Equal("model", captured!.ModelId);
        Assert.Equal(512, captured.InferenceConfig.MaxTokens);
        Assert.Null(captured.InferenceConfig.Temperature);
        var fields = captured.AdditionalModelRequestFields.AsDictionary();
        Assert.Equal("applied", fields["directory_marker"].AsString());

        async IAsyncEnumerable<BedrockProtocolEvent> Capture(
            ConverseStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            captured = request;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return BedrockProtocolEvent.MessageStart("assistant");
            yield return BedrockProtocolEvent.MessageStop("end_turn");
            yield return BedrockProtocolEvent.Usage(1, 1);
        }
    }

    [Fact]
    public async Task BedrockRemoteHttpServiceUrlRequiresExplicitOptIn()
    {
        var calls = 0;
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory(
                "amazon-bedrock",
                BuiltInGameModelApis.BedrockConverseStream,
                "http://bedrock.invalid"));
        var configuration = new GameModelProviderTransportConfiguration
        {
            BedrockTransport = Transport,
        };
        options.ProviderConfigurations.Add("amazon-bedrock", configuration);
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "amazon-bedrock",
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("HTTPS", failure.Response!.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, calls);

        async IAsyncEnumerable<BedrockProtocolEvent> Transport(
            ConverseStreamRequest _,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            calls++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return BedrockProtocolEvent.MessageStart("assistant");
        }
    }

    [Fact]
    public async Task UnsupportedUserAndToolImagesFailBeforeProviderSerialization()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiCompletions);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory("compatible", BuiltInGameModelApis.OpenAiCompletions, "https://catalog.invalid/v1"));
        options.Authentications.Add(
            "compatible",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "key")));
        var runtime = new BuiltInGameModelRuntime(options);
        var call = new ToolCallContent("call-1", "inspect", "{}");
        var messages = new AgentMessage[]
        {
            new(
                AgentRole.User,
                new AgentContent[]
                {
                    new TextContent("look"),
                    new BinaryContent(AgentMediaKind.Image, "aW1hZ2U=", "image/png"),
                },
                DateTimeOffset.UnixEpoch),
            new(
                AgentRole.Assistant,
                new AgentContent[] { call },
                DateTimeOffset.UnixEpoch,
                model: "model",
                stopReason: ModelStopReason.ToolUse,
                provider: "compatible",
                api: BuiltInGameModelApis.OpenAiCompletions),
            AgentMessage.ToolResult(
                call,
                new ToolResult(new AgentContent[]
                {
                    new BinaryContent(AgentMediaKind.Image, "dG9vbA==", "image/png"),
                }),
                DateTimeOffset.UnixEpoch),
        };
        var request = new ModelRequest(
            "model",
            string.Empty,
            messages,
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            null,
            "run",
            1);

        var events = await CollectAsync(runtime.StreamAsync(
            "compatible",
            request,
            TestContext.Current.CancellationToken));

        var terminal = Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(ModelStreamEventKind.Failed, terminal.Kind);
        Assert.Contains("does not declare image input support", terminal.Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(handler.Body);
        Assert.DoesNotContain("aW1hZ2U=", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("dG9vbA==", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingConfigurationIsAnInBandTerminalFailure()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1"));
        options.Authentications.Add(
            "openai",
            new StaticGameProviderAuthentication(configured: false));
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("not configured", failure.Response!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownApiIsAnInBandTerminalFailure()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory("custom", "unknown-wire-api", "https://catalog.invalid/v1"));
        options.Authentications.Add("custom", new StaticGameProviderAuthentication());
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "custom",
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("unsupported API", failure.Response!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealtimeOnlyModelsRemainInspectableButAreNotExecutableThroughResponsesHttp()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory(
                "openai",
                BuiltInGameModelApis.OpenAiResponses,
                "https://openai.invalid/v1",
                modelId: "gpt-realtime-2.1"));
        options.Authentications.Add("openai", new StaticGameProviderAuthentication());
        var runtime = new BuiltInGameModelRuntime(options);

        Assert.Single(runtime.Directory.GetModels("openai"));
        Assert.Null(runtime.Catalog.GetModel("openai", "gpt-realtime-2.1"));
        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(model: "gpt-realtime-2.1"),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("no longer registered", failure.Response!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeferredResolverFailureDoesNotEscapeAsyncSetup()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1"));
        options.Authentications.Add("openai", new StaticGameProviderAuthentication());
        options.ResolveConfigurationAsync = async (_, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("resolver failed after await");
        };
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Equal("resolver failed after await", failure.Response!.ErrorMessage);
    }

    [Theory]
    [InlineData("header-name")]
    [InlineData("header-nul")]
    [InlineData("header-length")]
    [InlineData("option-key")]
    [InlineData("option-nul")]
    public async Task InvalidRequestConfigurationBecomesAnInBandTerminalFailure(string invalidField)
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1"));
        options.Authentications.Add("openai", new StaticGameProviderAuthentication());
        options.ResolveConfigurationAsync = (_, _) =>
        {
            var configuration = new GameModelProviderTransportConfiguration();
            switch (invalidField)
            {
                case "header-name":
                    configuration.Headers["Bad Header"] = "value";
                    break;
                case "header-nul":
                    configuration.Headers["X-Test"] = "value\0";
                    break;
                case "header-length":
                    configuration.Headers["X-Test"] = new string('x', 65_537);
                    break;
                case "option-key":
                    configuration.Options[new string('k', 257)] = "value";
                    break;
                case "option-nul":
                    configuration.Options["test.option"] = "value\0";
                    break;
                default:
                    throw new InvalidOperationException("Unknown test case.");
            }

            return new ValueTask<GameModelProviderTransportConfiguration?>(configuration);
        };
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("invalid", failure.Response!.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task AuthStaticAndRequestConfigurationMergeByFieldWithRequestWinning()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://directory.invalid/v1"));
        options.Authentications.Add(
            "openai",
            new FixedAuthentication(new GameProviderAuthResolution(
                new GameCredential(GameCredentialKind.ApiKey, "auth-credential"),
                "test-auth",
                new Uri("https://auth.invalid/v1"),
                new Dictionary<string, string?>
                {
                    ["X-Order"] = "auth",
                    ["Authorization"] = "Bearer auth-header",
                })));
        var staticConfiguration = new GameModelProviderTransportConfiguration
        {
            BaseUrl = new Uri("https://static.invalid/v1"),
        };
        staticConfiguration.Headers["x-order"] = "static";
        options.ProviderConfigurations.Add("openai", staticConfiguration);
        options.ResolveConfigurationAsync = async (_, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var requestConfiguration = new GameModelProviderTransportConfiguration
            {
                BaseUrl = new Uri("https://request.invalid/v1"),
            };
            requestConfiguration.Headers["X-ORDER"] = "request";
            requestConfiguration.Headers["authorization"] = "Bearer request-header";
            return requestConfiguration;
        };
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(),
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal("request.invalid", handler.RequestUri!.Host);
        Assert.Equal("request", handler.Header("X-Order"));
        Assert.Equal("Bearer request-header", handler.Header("Authorization"));
    }

    [Fact]
    public async Task RequestConfigurationCanDeleteHeadersFromEarlierLayers()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiResponses);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory(
                "openai",
                BuiltInGameModelApis.OpenAiResponses,
                "https://directory.invalid/v1",
                modelHeaders: new Dictionary<string, string?>
                {
                    ["X-Delete"] = "model",
                    ["X-Model"] = "kept",
                }));
        options.Authentications.Add(
            "openai",
            new FixedAuthentication(new GameProviderAuthResolution(
                new GameCredential(GameCredentialKind.ApiKey, "secret-never-added-to-the-request"),
                "test-auth",
                headers: new Dictionary<string, string?>
                {
                    ["X-Delete"] = "authentication",
                    ["X-Authentication"] = "kept",
                })));
        var providerConfiguration = new GameModelProviderTransportConfiguration();
        providerConfiguration.Headers["X-Delete"] = "provider";
        providerConfiguration.Headers["X-Provider"] = "kept";
        options.ProviderConfigurations.Add("openai", providerConfiguration);
        ModelRequest? requestSeenByResolver = null;
        options.ResolveConfigurationAsync = (context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestSeenByResolver = context.Request;
            var requestConfiguration = new GameModelProviderTransportConfiguration();
            requestConfiguration.Headers["x-delete"] = null;
            requestConfiguration.Headers["X-Authentication"] = null;
            return new ValueTask<GameModelProviderTransportConfiguration?>(requestConfiguration);
        };
        var request = Request();
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            request,
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Same(request, requestSeenByResolver);
        Assert.Null(handler.Header("X-Delete"));
        Assert.Null(handler.Header("X-Authentication"));
        Assert.Equal("kept", handler.Header("X-Model"));
        Assert.Equal("kept", handler.Header("X-Provider"));
        Assert.DoesNotContain("secret-never-added-to-the-request", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BuiltInGameModelApis.OpenAiResponses, "Host", null)]
    [InlineData(BuiltInGameModelApis.OpenAiResponses, "Content-Length", "1")]
    [InlineData(BuiltInGameModelApis.BedrockConverseStream, "Authorization", null)]
    [InlineData(BuiltInGameModelApis.BedrockConverseStream, "x-amz-security-token", "attacker")]
    public async Task TransportControlledHeadersCannotBeConfiguredOrDeleted(
        string api,
        string header,
        string? value)
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiResponses);
        using var client = new HttpClient(handler);
        var providerId = api == BuiltInGameModelApis.BedrockConverseStream ? "amazon-bedrock" : "openai";
        var options = Options(client, Directory(providerId, api, "https://provider.invalid/v1"));
        if (api == BuiltInGameModelApis.OpenAiResponses)
        {
            options.Authentications.Add(
                providerId,
                new StaticGameProviderAuthentication(
                    credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        }

        var configuration = new GameModelProviderTransportConfiguration();
        configuration.Headers[header] = value;
        configuration.Options[BuiltInGameModelConfigurationKeys.AwsSkipAuthentication] = "true";
        configuration.Options[BuiltInGameModelConfigurationKeys.AwsRegion] = "us-east-1";
        options.ProviderConfigurations.Add(providerId, configuration);
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            providerId,
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("header", failure.Response!.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData(BuiltInGameModelApis.OpenAiResponses, "openai", "Host")]
    [InlineData(BuiltInGameModelApis.OpenAiCompletions, "compatible", "Content-Length")]
    public async Task ConfigurableCredentialHeaderCannotTargetTransportControlledHeader(
        string api,
        string providerId,
        string header)
    {
        var handler = new RecordingHandler(api);
        using var client = new HttpClient(handler);
        var options = Options(client, Directory(providerId, api, "https://provider.invalid/v1"));
        options.Authentications.Add(
            providerId,
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "secret")));
        var configuration = new GameModelProviderTransportConfiguration();
        configuration.Options[BuiltInGameModelConfigurationKeys.AuthenticationHeader] = header;
        options.ProviderConfigurations.Add(providerId, configuration);
        var runtime = new BuiltInGameModelRuntime(options);

        var events = await CollectAsync(runtime.StreamAsync(
            providerId,
            Request(),
            TestContext.Current.CancellationToken));

        var failure = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Contains("transport", failure.Response!.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task StoredAuthenticationFlowsThroughCatalogIntoProviderRequest()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiResponses);
        using var client = new HttpClient(handler);
        var store = new InMemoryGameCredentialStore();
        await store.SetAsync(
            new GameCredentialKey("openai"),
            new GameCredential(GameCredentialKind.ApiKey, "stored-credential"),
            TestContext.Current.CancellationToken);
        var authentication = new StoredGameProviderAuthentication("openai", store);
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1"));
        options.Authentications.Add("openai", authentication);
        var runtime = new BuiltInGameModelRuntime(options);

        var available = await runtime.Catalog.GetAvailableModelsAsync(
            "openai",
            TestContext.Current.CancellationToken);
        var response = await runtime.CompleteAsync(
            "openai",
            Request(),
            TestContext.Current.CancellationToken);

        Assert.Single(available);
        Assert.Equal(ModelStopReason.Stop, response.StopReason);
        Assert.Contains("stored-credential", handler.Header("Authorization"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationInterruptsNonCooperativeAuthenticationInsteadOfBecomingFailureEvent()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1"));
        options.Authentications.Add("openai", new NonCooperativeAuthentication());
        var runtime = new BuiltInGameModelRuntime(options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in runtime.StreamAsync("openai", Request(), cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public async Task CancellationInterruptsNonCooperativeRequestConfigurationResolver()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1"));
        options.Authentications.Add(
            "openai",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        var pending = new TaskCompletionSource<GameModelProviderTransportConfiguration?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        options.ResolveConfigurationAsync = (_, _) => new ValueTask<GameModelProviderTransportConfiguration?>(pending.Task);
        var runtime = new BuiltInGameModelRuntime(options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in runtime.StreamAsync("openai", Request(), cancellation.Token))
            {
            }
        });

        pending.TrySetException(new InvalidOperationException("late resolver failure"));
    }

    [Fact]
    public async Task RawProviderPreservesStructuredFailureWhileRuntimeStreamProvidesInBandBoundary()
    {
        var handler = new RecordingHandler(
            BuiltInGameModelApis.OpenAiResponses,
            HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":\"insufficient_quota\"}}",
            response => response.Headers.TryAddWithoutValidation("retry-after", "10"));
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1"));
        options.Authentications.Add(
            "openai",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        var runtime = new BuiltInGameModelRuntime(options);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(runtime.CreateProvider("openai").StreamAsync(
                Request(),
                TestContext.Current.CancellationToken)));
        var boundary = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(),
            TestContext.Current.CancellationToken));

        Assert.Equal(429, exception.StatusCode);
        Assert.False(exception.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(10), exception.RetryAfter);
        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(boundary).Kind);
    }

    [Fact]
    public async Task ConsumerStoppingAtTerminalDisposesTheInnerProviderStream()
    {
        var provider = new TrackingProvider();
        var runtime = RuntimeWithProvider(provider);

        await foreach (var streamEvent in runtime.StreamAsync(
                           "openai",
                           Request(),
                           TestContext.Current.CancellationToken))
        {
            if (streamEvent.IsTerminal)
            {
                break;
            }
        }

        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task ConsumerStoppingBeforeTerminalDisposesTheInnerProviderStream()
    {
        var provider = new TrackingProvider();
        var runtime = RuntimeWithProvider(provider);

        await foreach (var _ in runtime.StreamAsync(
                           "openai",
                           Request(),
                           TestContext.Current.CancellationToken))
        {
            break;
        }

        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task HostileInnerDisposeCannotReplaceTerminalOrAddAnotherFailure()
    {
        var provider = new TrackingProvider(throwOnDispose: true);
        var runtime = RuntimeWithProvider(provider);

        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(),
            TestContext.Current.CancellationToken));

        Assert.Equal(2, events.Count);
        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task UnknownProviderAndModelAreInBandTerminalFailures()
    {
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1"));
        options.Authentications.Add(
            "openai",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "key")));
        var runtime = new BuiltInGameModelRuntime(options);

        var unknownProvider = await CollectAsync(runtime.StreamAsync(
            "missing",
            Request(),
            TestContext.Current.CancellationToken));
        var unknownModel = await CollectAsync(runtime.StreamAsync(
            "openai",
            Request(model: "missing"),
            TestContext.Current.CancellationToken));

        Assert.Contains("no longer registered", Assert.Single(unknownProvider).Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("no longer registered", Assert.Single(unknownModel).Response!.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(OpenAiCompatibleFamilies))]
    public async Task BundledOpenAiCompatibleCompatibilityReachesTheWire(
        string providerId,
        string modelId,
        string maximumTokenField,
        string systemRole,
        string reasoningShape,
        bool sendsStore,
        bool supportsLongCache,
        bool sendsSessionAffinity)
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiCompletions);
        using var client = new HttpClient(handler);
        var options = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = name => name switch
            {
                "CLOUDFLARE_ACCOUNT_ID" => "account",
                "CLOUDFLARE_GATEWAY_ID" => "gateway",
                _ => null,
            },
        };
        options.Authentications.Add(
            providerId,
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        var runtime = new BuiltInGameModelRuntime(options);
        var selection = runtime.Catalog.Resolve(
            providerId,
            modelId,
            reasoning: GameReasoningLevel.High);
        var parameters = selection.CreateParameters(new ModelParameters
        {
            MaxOutputTokens = 123,
            CacheRetention = ModelCacheRetention.Long,
        });
        var request = new ModelRequest(
            modelId,
            "rules",
            Array.Empty<AgentMessage>(),
            new[] { new ToolDefinition("inspect", "Inspect", "{\"type\":\"object\"}") },
            parameters,
            "session-1",
            "run",
            1);

        var events = await CollectAsync(runtime.StreamAsync(
            providerId,
            request,
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        using var document = JsonDocument.Parse(handler.Body);
        var root = document.RootElement;
        Assert.Equal(123, root.GetProperty(maximumTokenField).GetInt32());
        Assert.False(root.TryGetProperty(
            maximumTokenField == "max_tokens" ? "max_completion_tokens" : "max_tokens",
            out _));
        Assert.Equal(systemRole, root.GetProperty("messages")[0].GetProperty("role").GetString());
        if (sendsStore)
        {
            Assert.False(root.GetProperty("store").GetBoolean());
        }
        else
        {
            Assert.False(root.TryGetProperty("store", out _));
        }

        if (supportsLongCache)
        {
            Assert.Equal("24h", root.GetProperty("prompt_cache_retention").GetString());
        }
        else
        {
            Assert.False(root.TryGetProperty("prompt_cache_retention", out _));
        }

        switch (reasoningShape)
        {
            case "zai":
                Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
                Assert.False(root.GetProperty("thinking").GetProperty("clear_thinking").GetBoolean());
                Assert.True(root.GetProperty("tool_stream").GetBoolean());
                break;
            case "deepseek":
                Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
                Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
                break;
            case "deepseek-toggle":
                Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
                Assert.False(root.TryGetProperty("reasoning_effort", out _));
                break;
            case "together":
                Assert.True(root.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
                Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
                break;
            case "openrouter":
                Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.False(root.TryGetProperty("reasoning_effort", out _));
                break;
            case "effort":
                Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
                break;
            case "baseten":
                Assert.True(root.GetProperty("chat_template_args").GetProperty("enable_thinking").GetBoolean());
                Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
                break;
            case "none":
                Assert.False(root.TryGetProperty("thinking", out _));
                Assert.False(root.TryGetProperty("reasoning", out _));
                Assert.False(root.TryGetProperty("reasoning_effort", out _));
                break;
            default:
                throw new InvalidOperationException("Unknown reasoning fixture.");
        }

        Assert.Equal(sendsSessionAffinity ? "session-1" : null, handler.Header("x-session-affinity"));
        if (providerId == "nvidia")
        {
            Assert.Equal("3600", handler.Header("NVCF-POLL-SECONDS"));
        }

        Assert.DoesNotContain("${", handler.RequestUri!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BundledDeepSeekClassifierShapeDisablesThinkingWithoutInvalidEffort()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiCompletions);
        using var client = new HttpClient(handler);
        var options = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = _ => null,
        };
        options.Authentications.Add(
            "deepseek",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        var runtime = new BuiltInGameModelRuntime(options);
        var request = new ModelRequest(
            "deepseek-v4-pro",
            "classify",
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters
            {
                Temperature = 0,
                MaxOutputTokens = 128,
                ReasoningLevel = "off",
            },
            "session",
            "route",
            1);

        var events = await CollectAsync(runtime.CreateProvider("deepseek").StreamAsync(
            request,
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        using var document = JsonDocument.Parse(handler.Body);
        var root = document.RootElement;
        Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("max_completion_tokens", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task BundledXaiResponsesCompatibilityReachesTheWire()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiResponses);
        using var client = new HttpClient(handler);
        var options = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = _ => null,
        };
        options.Authentications.Add(
            "xai",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        var runtime = new BuiltInGameModelRuntime(options);
        var selection = runtime.Catalog.Resolve(
            "xai",
            "grok-4.5",
            reasoning: GameReasoningLevel.High);
        var parameters = selection.CreateParameters(new ModelParameters
        {
            MaxOutputTokens = 123,
            CacheRetention = ModelCacheRetention.Long,
        });
        var request = new ModelRequest(
            "grok-4.5",
            "rules",
            Array.Empty<AgentMessage>(),
            new[] { new ToolDefinition("inspect", "Inspect", "{\"type\":\"object\"}") },
            parameters,
            "session-1",
            "run",
            1);

        var events = await CollectAsync(runtime.StreamAsync(
            "xai",
            request,
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.EndsWith("/responses", handler.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(handler.Body);
        var root = document.RootElement;
        Assert.Equal(123, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("developer", root.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.False(root.GetProperty("tools")[0].TryGetProperty("strict", out _));
        Assert.False(root.TryGetProperty("prompt_cache_retention", out _));
    }

    [Fact]
    public async Task BundledAnthropicAdaptiveStrictAndCacheCompatibilityReachesTheWire()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.AnthropicMessages);
        using var client = new HttpClient(handler);
        var options = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = _ => null,
        };
        options.Authentications.Add(
            "anthropic",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        var runtime = new BuiltInGameModelRuntime(options);
        var selection = runtime.Catalog.Resolve(
            "anthropic",
            "claude-opus-4-6",
            reasoning: GameReasoningLevel.High);
        var parameters = selection.CreateParameters(new ModelParameters
        {
            CacheRetention = ModelCacheRetention.Long,
        });
        var strict = new ToolDefinition(
            "inspect",
            "Inspect",
            "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}}}",
            ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require));
        var request = new ModelRequest(
            "claude-opus-4-6",
            "rules",
            Array.Empty<AgentMessage>(),
            new[] { strict },
            parameters,
            "session-1",
            "run",
            1);

        var events = await CollectAsync(runtime.StreamAsync(
            "anthropic",
            request,
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        using var document = JsonDocument.Parse(handler.Body);
        var root = document.RootElement;
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("1h", root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.True(root.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task BundledOpenAiStrictGrammarDeferredAndExplicitCacheCompatibilityReachesTheWire()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.OpenAiResponses);
        using var client = new HttpClient(handler);
        var options = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = _ => null,
        };
        options.Authentications.Add(
            "openai",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        var runtime = new BuiltInGameModelRuntime(options);
        var selection = runtime.Catalog.Resolve(
            "openai",
            "gpt-5.6-sol",
            reasoning: GameReasoningLevel.High);
        var parameters = selection.CreateParameters(new ModelParameters
        {
            CacheRetention = ModelCacheRetention.None,
        });
        var inspect = new ToolDefinition(
            "inspect",
            "Inspect",
            "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}",
            ToolConstrainedSampling.Grammar(openAiRegex: "[a-z]+"));
        var move = new ToolDefinition(
            "move",
            "Move",
            "{\"type\":\"object\"}",
            ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require));
        var call = new ToolCallContent("call_inspect|fc_inspect", "inspect", "{\"value\":\"x\"}");
        var messages = new AgentMessage[]
        {
            new(
                AgentRole.Assistant,
                new AgentContent[] { call },
                DateTimeOffset.UnixEpoch,
                model: "gpt-5.6-sol",
                stopReason: ModelStopReason.ToolUse,
                provider: "openai",
                api: BuiltInGameModelApis.OpenAiResponses),
            AgentMessage.ToolResult(
                call,
                new ToolResult(new AgentContent[] { new TextContent("ok") }, addedToolNames: new[] { "move" }),
                DateTimeOffset.UnixEpoch),
        };
        var request = new ModelRequest(
            "gpt-5.6-sol",
            "rules",
            messages,
            new[] { inspect, move },
            parameters,
            "session-1",
            "run",
            1);

        var events = await CollectAsync(runtime.StreamAsync(
            "openai",
            request,
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        using var document = JsonDocument.Parse(handler.Body);
        var root = document.RootElement;
        Assert.Equal("explicit", root.GetProperty("prompt_cache_options").GetProperty("mode").GetString());
        Assert.Equal("custom", root.GetProperty("tools")[0].GetProperty("type").GetString());
        var additional = Assert.Single(root.GetProperty("input").EnumerateArray(), item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "additional_tools");
        Assert.True(additional.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task GoogleLegacyToolSchemaCompatibilityReachesTheWire()
    {
        var handler = new RecordingHandler(BuiltInGameModelApis.GoogleGenerativeAi);
        using var client = new HttpClient(handler);
        var options = Options(
            client,
            Directory(
                "google",
                BuiltInGameModelApis.GoogleGenerativeAi,
                "https://google.invalid/v1beta",
                compatibility: new Dictionary<string, object?>
                {
                    ["useLegacyOpenApiToolSchemas"] = true,
                }));
        options.Authentications.Add(
            "google",
            new StaticGameProviderAuthentication(
                credential: new GameCredential(GameCredentialKind.ApiKey, "test-key")));
        var runtime = new BuiltInGameModelRuntime(options);
        var tool = new ToolDefinition(
            "inspect",
            "Inspect",
            "{\"$schema\":\"draft\",\"type\":\"object\",\"properties\":{\"path\":{\"$id\":\"nested\",\"type\":\"string\"}}}");
        var request = new ModelRequest(
            "model",
            "rules",
            Array.Empty<AgentMessage>(),
            new[] { tool },
            new ModelParameters(),
            "session-1",
            "run",
            1);

        var events = await CollectAsync(runtime.StreamAsync(
            "google",
            request,
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        using var document = JsonDocument.Parse(handler.Body);
        var declaration = document.RootElement.GetProperty("tools")[0]
            .GetProperty("functionDeclarations")[0];
        Assert.True(declaration.TryGetProperty("parameters", out var parameters));
        Assert.False(declaration.TryGetProperty("parametersJsonSchema", out _));
        Assert.False(parameters.TryGetProperty("$schema", out _));
        Assert.False(parameters.GetProperty("properties").GetProperty("path").TryGetProperty("$id", out _));
    }

    [Fact]
    public async Task BedrockStrictToolCompatibilityReachesTheWire()
    {
        ConverseStreamRequest? captured = null;
        using var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var options = Options(
            client,
            Directory(
                "amazon-bedrock",
                BuiltInGameModelApis.BedrockConverseStream,
                "https://bedrock.invalid",
                compatibility: new Dictionary<string, object?>
                {
                    ["supportsStrictMode"] = true,
                }));
        var transport = new GameModelProviderTransportConfiguration
        {
            BedrockTransport = Capture,
        };
        transport.Options[BuiltInGameModelConfigurationKeys.AwsRegion] = "us-east-1";
        options.ProviderConfigurations.Add("amazon-bedrock", transport);
        var runtime = new BuiltInGameModelRuntime(options);
        var strict = new ToolDefinition(
            "inspect",
            "Inspect",
            "{\"type\":\"object\"}",
            ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require));
        var request = new ModelRequest(
            "model",
            "rules",
            Array.Empty<AgentMessage>(),
            new[] { strict },
            new ModelParameters(),
            "session-1",
            "run",
            1);

        var events = await CollectAsync(runtime.StreamAsync(
            "amazon-bedrock",
            request,
            TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events, item => item.IsTerminal).Kind);
        Assert.True(Assert.Single(captured!.ToolConfig.Tools).ToolSpec.Strict);

        async IAsyncEnumerable<BedrockProtocolEvent> Capture(
            ConverseStreamRequest value,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            captured = value;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return BedrockProtocolEvent.MessageStart("assistant");
            yield return BedrockProtocolEvent.MessageStop("end_turn");
            yield return BedrockProtocolEvent.Usage(1, 1);
        }
    }

    private static BuiltInGameModelRuntimeOptions Options(
        HttpClient client,
        GameModelDirectorySnapshot directory) =>
        new(client)
        {
            Directory = directory,
            GetEnvironmentVariable = _ => null,
        };

    private static BuiltInGameModelRuntime RuntimeWithProvider(IModelProvider provider)
    {
        var client = new HttpClient(new RecordingHandler(BuiltInGameModelApis.OpenAiResponses));
        var runtime = new BuiltInGameModelRuntime(Options(
            client,
            Directory("openai", BuiltInGameModelApis.OpenAiResponses, "https://catalog.invalid/v1")));
        var current = runtime.Catalog.GetProvider("openai")!;
        runtime.Catalog.Register(
            new GameModelProviderRegistration(
                current.Descriptor,
                provider,
                new StaticGameProviderAuthentication(configured: true, source: "test"),
                current.Models,
                catalogVersion: current.CatalogVersion),
            replace: true);
        return runtime;
    }

    private static GameModelDirectorySnapshot Directory(
        string providerId,
        string api,
        string endpoint,
        string? environmentVariables = null,
        string modelId = "model",
        Dictionary<string, object?>? compatibility = null,
        Dictionary<string, string?>? modelHeaders = null,
        Dictionary<string, object?>? cost = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = "test",
            generatedAt = "2026-08-08T00:00:00Z",
            providers = new[]
            {
                new
                {
                    id = providerId,
                    name = providerId,
                    endpoint,
                    metadata = environmentVariables is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>
                        {
                            [BuiltInGameModelConfigurationKeys.EnvironmentVariablesMetadata] = environmentVariables,
                        },
                    models = new[]
                    {
                        new
                        {
                            id = modelId,
                            name = "Model",
                            api,
                            contextWindow = 8192,
                            maximumOutput = 512,
                            input = new[] { "text" },
                            output = new[] { "text", "tools" },
                            cost = cost ?? new Dictionary<string, object?>(),
                            sampling = new Dictionary<string, object?>
                            {
                                ["directory_marker"] = "applied",
                            },
                            headers = modelHeaders ?? new Dictionary<string, string?>
                            {
                                ["X-Directory"] = "catalog",
                            },
                            compatibility = compatibility ?? new Dictionary<string, object?>
                            {
                                ["supportsTemperature"] = false,
                                ["structuredOutput"] = true,
                                ["interleaved"] = api == BuiltInGameModelApis.OpenAiCompletions
                                    ? new Dictionary<string, string> { ["field"] = "reasoning_details" }
                                    : false,
                            },
                        },
                    },
                },
            },
        });
        return GameModelDirectory.ParseJson(json);
    }

    private static GameModelDirectorySnapshot MixedApiDirectory()
    {
        var json = JsonSerializer.Serialize(new
        {
            version = "test",
            generatedAt = "2026-08-08T00:00:00Z",
            providers = new[]
            {
                new
                {
                    id = "mixed",
                    name = "Mixed",
                    endpoint = "https://mixed.invalid/v1",
                    models = new[]
                    {
                        new
                        {
                            id = "responses-model",
                            name = "Responses",
                            api = BuiltInGameModelApis.OpenAiResponses,
                            contextWindow = 8192,
                            maximumOutput = 512,
                            input = new[] { "text" },
                            output = new[] { "text", "tools" },
                        },
                        new
                        {
                            id = "completions-model",
                            name = "Completions",
                            api = BuiltInGameModelApis.OpenAiCompletions,
                            contextWindow = 8192,
                            maximumOutput = 512,
                            input = new[] { "text" },
                            output = new[] { "text", "tools" },
                        },
                    },
                },
            },
        });
        return GameModelDirectory.ParseJson(json);
    }

    private static GameModelDirectorySnapshot MediaCapabilityDirectory()
    {
        const string json = """
            {
              "version": "test",
              "generatedAt": "2026-08-08T00:00:00Z",
              "providers": [{
                "id": "media",
                "name": "Media",
                "endpoint": "https://media.invalid/v1",
                "models": [{
                  "id": "media-model",
                  "name": "Media Model",
                  "api": "openai-completions",
                  "contextWindow": 8192,
                  "maximumOutput": 512,
                  "input": ["text", "image", "audio", "video", "structured"],
                  "output": ["text", "image", "audio", "video", "structured", "tools"]
                }]
              }]
            }
            """;
        return GameModelDirectory.ParseJson(json);
    }

    private static ModelRequest Request(
        string model = "model",
        double? temperature = null,
        int? maxOutputTokens = null) =>
        new(
            model,
            "system",
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters
            {
                Temperature = temperature,
                MaxOutputTokens = maxOutputTokens,
            },
            "session",
            "run",
            1);

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static int Occurrences(string value, string expected)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        return count;
    }

    private static string CodexToken(string accountId)
    {
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return Encode("{\"alg\":\"none\"}")
               + "."
               + Encode(JsonSerializer.Serialize(new Dictionary<string, object?>
               {
                   ["https://api.openai.com/auth"] = new Dictionary<string, string>
                   {
                       ["chatgpt_account_id"] = accountId,
                   },
               }))
               + ".signature";
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _api;
        private readonly HttpStatusCode _status;
        private readonly string? _responseBody;
        private readonly Action<HttpResponseMessage>? _configureResponse;
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public RecordingHandler(
            string api,
            HttpStatusCode status = HttpStatusCode.OK,
            string? responseBody = null,
            Action<HttpResponseMessage>? configureResponse = null)
        {
            _api = api;
            _status = status;
            _responseBody = responseBody;
            _configureResponse = configureResponse;
        }

        public Uri? RequestUri { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
            {
                _headers[header.Key] = string.Join(",", header.Value);
            }

            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody ?? ResponseBody(_api), Encoding.UTF8, "text/event-stream"),
            };
            _configureResponse?.Invoke(response);
            return response;
        }

        private static string ResponseBody(string api) => api switch
        {
            BuiltInGameModelApis.AzureOpenAiResponses
                or BuiltInGameModelApis.OpenAiCodexResponses
                or BuiltInGameModelApis.OpenAiResponses =>
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"model\":\"model\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}}}\n\n",
            BuiltInGameModelApis.OpenAiCompletions =>
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            BuiltInGameModelApis.AnthropicMessages => """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_1","model":"model","usage":{"input_tokens":0,"output_tokens":0}}}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":0}}

                event: message_stop
                data: {"type":"message_stop"}

                """,
            BuiltInGameModelApis.GoogleGenerativeAi or BuiltInGameModelApis.GoogleVertex =>
                "data: {\"responseId\":\"response-1\",\"candidates\":[{\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1,\"totalTokenCount\":2}}\n\n",
            BuiltInGameModelApis.MistralConversations =>
                "data: {\"id\":\"response-1\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}\n\n",
            _ => throw new InvalidOperationException("No test response is defined for API '" + api + "'."),
        };
    }

    private sealed class FixedAuthentication : IGameProviderAuthentication
    {
        private readonly GameProviderAuthResolution _resolution;

        public FixedAuthentication(GameProviderAuthResolution resolution)
        {
            _resolution = resolution;
        }

        public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

        public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameProviderAuthStatus>(new GameProviderAuthStatus(
                true,
                _resolution.Source,
                _resolution.Credential?.Kind));
        }

        public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameProviderAuthResolution?>(_resolution);
        }

        public ValueTask<GameCredential> LoginAsync(
            string scheme,
            GameAuthInteraction interaction,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fixed test authentication cannot log in.");

        public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fixed test authentication cannot log out.");
    }

    private sealed class NonCooperativeAuthentication : IGameProviderAuthentication
    {
        private readonly TaskCompletionSource<GameProviderAuthStatus> _pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

        public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken) =>
            new(_pending.Task);

        public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Resolution must not run while the check is pending.");

        public ValueTask<GameCredential> LoginAsync(
            string scheme,
            GameAuthInteraction interaction,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The non-cooperative test authentication cannot log in.");

        public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The non-cooperative test authentication cannot log out.");
    }

    private sealed class TrackingProvider : IModelProvider
    {
        private readonly bool _throwOnDispose;
        private int _disposeCount;

        public TrackingProvider(bool throwOnDispose = false)
        {
            _throwOnDispose = throwOnDispose;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken) =>
            new TrackingStream(this, cancellationToken);

        private sealed class TrackingStream : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
        {
            private readonly TrackingProvider _owner;
            private readonly CancellationToken _cancellationToken;
            private int _index;

            public TrackingStream(TrackingProvider owner, CancellationToken cancellationToken)
            {
                _owner = owner;
                _cancellationToken = cancellationToken;
            }

            public ModelStreamEvent Current => _index switch
            {
                1 => ModelStreamEvent.Update(
                    ModelStreamEventKind.Started,
                    new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending)),
                2 => ModelStreamEvent.Terminal(new ModelResponse(
                    new AgentContent[] { new TextContent("done") },
                    ModelStopReason.Stop)),
                _ => throw new InvalidOperationException("The stream has no current event."),
            };

            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(
                CancellationToken cancellationToken = default) => this;

            public ValueTask<bool> MoveNextAsync()
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _index++;
                return new ValueTask<bool>(_index <= 2);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _owner._disposeCount);
                return _owner._throwOnDispose
                    ? ValueTask.FromException(new InvalidOperationException("hostile dispose"))
                    : ValueTask.CompletedTask;
            }
        }
    }
}
