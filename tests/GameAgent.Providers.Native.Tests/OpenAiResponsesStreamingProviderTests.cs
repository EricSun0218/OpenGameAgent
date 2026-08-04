using System.Text.Json;
using GameAgent.Core;
using GameAgent.Providers.Native;
using Xunit;

namespace GameAgent.Providers.Native.Tests;

public sealed class OpenAiResponsesStreamingProviderTests
{
    [Fact]
    public async Task EncodesFullToolTranscriptAndNormalizesStream()
    {
        var sse =
            "event: response.output_item.added\n" +
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call-new\",\"name\":\"move\"}}\n\n" +
            "event: response.function_call_arguments.delta\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{\\\"x\\\":4}\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":12,\"output_tokens\":5,\"total_tokens\":17,\"input_tokens_details\":{\"cached_tokens\":2},\"output_tokens_details\":{\"reasoning_tokens\":1}}}}\n\n";
        var transport = new FakeTransport(sse);
        var provider = new OpenAiResponsesStreamingProvider(
            Options(),
            new StaticNativeApiCredentialSource("secret"),
            transport);

        var events = await NativeProviderTestData.ReadAsync(
            provider,
            NativeProviderTestData.Request());

        Assert.Equal("Authorization", transport.CredentialHeaderName);
        Assert.Equal(4, events.Count);
        Assert.Equal(
            new long[] { 0, 1, 2, 3 },
            events.Select(item => item.Ordinal));
        Assert.Equal(ModelStreamEventKinds.ToolCallDelta, events[0].Kind);
        Assert.Equal("move", events[0].ToolNameDelta);
        Assert.Equal("{\"x\":4}", events[1].ArgumentsJsonDelta);
        Assert.Equal(2, events[2].Usage!.CacheReadTokens);
        Assert.Equal("tool_calls", events[3].FinishReason);

        using var body = JsonDocument.Parse(transport.RequestBody!);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        var input = body.RootElement.GetProperty("input");
        Assert.Contains(
            input.EnumerateArray(),
            item => item.GetProperty("type").GetString() == "function_call");
        Assert.Contains(
            input.EnumerateArray(),
            item => item.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal(
            "function",
            body.RootElement.GetProperty("tools")[0]
                .GetProperty("type").GetString());
    }

    [Fact]
    public async Task RejectsTruncatedStream()
    {
        var transport = new FakeTransport(
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"x\"}\n\n");
        var provider = new OpenAiResponsesStreamingProvider(
            Options(),
            new StaticNativeApiCredentialSource("secret"),
            transport);

        var exception = await Assert.ThrowsAsync<ProviderException>(async () =>
            await NativeProviderTestData.ReadAsync(
                provider,
                NativeProviderTestData.Request()));

        Assert.Equal("provider_stream_terminal_missing", exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public void RequiresExplicitModelRouteDeclarationForReasoningEffort()
    {
        var conservative = new OpenAiResponsesStreamingProvider(
            Options(),
            new StaticNativeApiCredentialSource("secret"),
            new FakeTransport(string.Empty));
        var options = Options();
        options.SupportsReasoningEffort = true;
        options.DefaultReasoningEffort = "medium";
        var declared = new OpenAiResponsesStreamingProvider(
            options,
            new StaticNativeApiCredentialSource("secret"),
            new FakeTransport(string.Empty));

        Assert.False(conservative.Capabilities.ReasoningEffort);
        Assert.True(declared.Capabilities.ReasoningEffort);
        Assert.NotEqual(
            conservative.RouteMetadata.RoutePolicyDigest,
            declared.RouteMetadata.RoutePolicyDigest);
    }

    [Fact]
    public async Task MapsMaximumReasoningToHighestSupportedRouteEffort()
    {
        var transport = new FakeTransport(
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n\n");
        var options = Options();
        options.SupportsReasoningEffort = true;
        var provider = new OpenAiResponsesStreamingProvider(
            options,
            new StaticNativeApiCredentialSource("secret"),
            transport);
        var request = NativeProviderTestData.Request();
        request.Inference = new ModelInferenceOptions
        {
            ReasoningEffort = ModelReasoningEfforts.Maximum
        };

        _ = await NativeProviderTestData.ReadAsync(provider, request);

        using var body = JsonDocument.Parse(transport.RequestBody!);
        Assert.Equal(
            ModelReasoningEfforts.ExtraHigh,
            body.RootElement.GetProperty("reasoning")
                .GetProperty("effort")
                .GetString());
    }

    private static OpenAiResponsesProviderOptions Options() => new()
    {
        Model = "gpt-test",
        BaseUri = new Uri("http://localhost:8041/v1"),
        AllowInsecureLoopback = true,
        MaxContextTokens = 100_000,
        MaxOutputTokens = 2_048
    };
}
