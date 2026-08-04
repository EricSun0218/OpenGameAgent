using System.Text.Json;
using GameAgent.Core;
using GameAgent.Providers.Native;
using Xunit;

namespace GameAgent.Providers.Native.Tests;

public sealed class GeminiInteractionsStreamingProviderTests
{
    [Fact]
    public async Task EncodesFullStepHistoryAndNormalizesStream()
    {
        var sse =
            "event: step.start\n" +
            "data: {\"type\":\"step.start\",\"step_index\":0,\"step\":{\"type\":\"function_call\",\"id\":\"call-new\",\"name\":\"move\"}}\n\n" +
            "event: step.delta\n" +
            "data: {\"type\":\"step.delta\",\"step_index\":0,\"delta\":{\"type\":\"arguments_delta\",\"arguments_delta\":\"{\\\"x\\\":5}\"}}\n\n" +
            "event: interaction.completed\n" +
            "data: {\"type\":\"interaction.completed\",\"interaction\":{\"status\":\"requires_action\",\"usage\":{\"input_tokens\":9,\"output_tokens\":4,\"total_tokens\":13}}}\n\n";
        var transport = new FakeTransport(sse);
        var provider = new GeminiInteractionsStreamingProvider(
            Options(),
            new StaticNativeApiCredentialSource("secret"),
            transport);

        var events = await NativeProviderTestData.ReadAsync(
            provider,
            NativeProviderTestData.Request());

        Assert.Equal("x-goog-api-key", transport.CredentialHeaderName);
        Assert.Equal(4, events.Count);
        Assert.Equal("move", events[0].ToolNameDelta);
        Assert.Equal("{\"x\":5}", events[1].ArgumentsJsonDelta);
        Assert.Equal(13, events[2].Usage!.ProviderTotalTokens);
        Assert.Equal("tool_calls", events[3].FinishReason);

        using var body = JsonDocument.Parse(transport.RequestBody!);
        Assert.Equal(
            "Be precise.",
            body.RootElement.GetProperty("system_instruction").GetString());
        var input = body.RootElement.GetProperty("input");
        Assert.Contains(
            input.EnumerateArray(),
            item => item.GetProperty("type").GetString() == "function_call");
        Assert.Contains(
            input.EnumerateArray(),
            item => item.GetProperty("type").GetString() == "function_result");
        Assert.Equal(
            "function",
            body.RootElement.GetProperty("tools")[0]
                .GetProperty("type").GetString());
    }

    [Fact]
    public async Task RejectsPromptCachingInsteadOfDroppingIt()
    {
        var provider = new GeminiInteractionsStreamingProvider(
            Options(),
            new StaticNativeApiCredentialSource("secret"),
            new FakeTransport(string.Empty));
        var request = NativeProviderTestData.Request();
        request.Inference = new ModelInferenceOptions
        {
            PromptCachingEnabled = true
        };

        var exception = await Assert.ThrowsAsync<ProviderException>(async () =>
            await NativeProviderTestData.ReadAsync(provider, request));

        Assert.Equal("provider_inference_control_unsupported", exception.Code);
    }

    [Fact]
    public async Task PreservesExplicitZeroUsageAndRejectsInconsistentEventType()
    {
        var zeroUsage = new FakeTransport(
            "event: interaction.completed\n" +
            "data: {\"type\":\"interaction.completed\",\"interaction\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"cached_tokens\":0,\"thought_tokens\":0,\"total_tokens\":0}}}\n\n");
        var provider = new GeminiInteractionsStreamingProvider(
            Options(),
            new StaticNativeApiCredentialSource("secret"),
            zeroUsage);

        var events = await NativeProviderTestData.ReadAsync(
            provider,
            NativeProviderTestData.Request());

        Assert.Equal(0, events[0].Usage!.CacheReadTokens);
        Assert.Equal(0, events[0].Usage!.ReasoningTokens);
        Assert.Equal(0, events[0].Usage!.ProviderTotalTokens);

        var inconsistent = new GeminiInteractionsStreamingProvider(
            Options(),
            new StaticNativeApiCredentialSource("secret"),
            new FakeTransport(
                "event: step.delta\n" +
                "data: {\"type\":\"interaction.completed\"}\n\n"));

        var exception = await Assert.ThrowsAsync<ProviderException>(async () =>
            await NativeProviderTestData.ReadAsync(
                inconsistent,
                NativeProviderTestData.Request()));

        Assert.Equal("provider_stream_protocol_error", exception.Code);
    }

    [Fact]
    public void RequiresExplicitModelRouteDeclarationForThinkingLevels()
    {
        var conservative = new GeminiInteractionsStreamingProvider(
            Options(),
            new StaticNativeApiCredentialSource("secret"),
            new FakeTransport(string.Empty));
        var options = Options();
        options.SupportsThinkingLevel = true;
        options.DefaultThinkingLevel = "medium";
        var declared = new GeminiInteractionsStreamingProvider(
            options,
            new StaticNativeApiCredentialSource("secret"),
            new FakeTransport(string.Empty));

        Assert.False(conservative.Capabilities.ReasoningEffort);
        Assert.True(declared.Capabilities.ReasoningEffort);
        Assert.NotEqual(
            conservative.RouteMetadata.RoutePolicyDigest,
            declared.RouteMetadata.RoutePolicyDigest);
    }

    private static GeminiInteractionsProviderOptions Options() => new()
    {
        Model = "gemini-test",
        BaseUri = new Uri("http://localhost:8042/v1beta"),
        AllowInsecureLoopback = true,
        MaxContextTokens = 100_000,
        MaxOutputTokens = 2_048
    };
}
