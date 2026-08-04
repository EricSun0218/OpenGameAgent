using GameAgent.Core;
using GameAgent.Providers.Native;
using Xunit;

namespace GameAgent.Providers.Native.Tests;

public sealed class NativeProviderConformanceTests
{
    [Fact]
    public async Task DifferentNativeDialectsProduceSameCanonicalTextResult()
    {
        var openAiTransport = new FakeTransport(
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":7,\"output_tokens\":2,\"total_tokens\":9}}}\n\n");
        var geminiTransport = new FakeTransport(
            "event: step.delta\n" +
            "data: {\"type\":\"step.delta\",\"step_index\":0,\"delta\":{\"type\":\"text\",\"text\":\"hello\"}}\n\n" +
            "event: interaction.completed\n" +
            "data: {\"type\":\"interaction.completed\",\"interaction\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":7,\"output_tokens\":2,\"total_tokens\":9}}}\n\n");
        var credential = new StaticNativeApiCredentialSource("secret");
        var openAi = new OpenAiResponsesStreamingProvider(
            new OpenAiResponsesProviderOptions
            {
                Model = "openai-test",
                BaseUri = new Uri("http://localhost:8043/v1"),
                AllowInsecureLoopback = true
            },
            credential,
            openAiTransport);
        var gemini = new GeminiInteractionsStreamingProvider(
            new GeminiInteractionsProviderOptions
            {
                Model = "gemini-test",
                BaseUri = new Uri("http://localhost:8044/v1beta"),
                AllowInsecureLoopback = true
            },
            credential,
            geminiTransport);

        var openAiEvents = await NativeProviderTestData.ReadAsync(
            openAi,
            NativeProviderTestData.Request());
        var geminiEvents = await NativeProviderTestData.ReadAsync(
            gemini,
            NativeProviderTestData.Request());

        Assert.Equal(
            openAiEvents.Select(Project),
            geminiEvents.Select(Project));
        Assert.Equal(ProviderRequestFamily.Responses,
            openAi.RouteMetadata.DialectContract.RequestFamily);
        Assert.Equal(ProviderRequestFamily.Interactions,
            gemini.RouteMetadata.DialectContract.RequestFamily);
        Assert.True(openAi.Capabilities.ToolCalling);
        Assert.True(gemini.Capabilities.StructuredInput);
        Assert.False(openAi.Capabilities.StatefulContinuation);
        Assert.False(gemini.Capabilities.StatefulContinuation);
    }

    [Fact]
    public async Task HttpAuthenticationFailureIsKnownZeroAndFallbackEligible()
    {
        var provider = new OpenAiResponsesStreamingProvider(
            new OpenAiResponsesProviderOptions
            {
                Model = "openai-test",
                BaseUri = new Uri("http://localhost:8045/v1"),
                AllowInsecureLoopback = true
            },
            new StaticNativeApiCredentialSource("secret"),
            new FakeTransport(string.Empty, statusCode: 401));

        var exception = await Assert.ThrowsAsync<ProviderException>(async () =>
            await NativeProviderTestData.ReadAsync(
                provider,
                NativeProviderTestData.Request()));

        Assert.Equal("provider_auth_failed", exception.Code);
        Assert.True(exception.UsageKnownToBeZero);
        Assert.True(exception.FallbackEligible);
    }

    [Fact]
    public async Task TerminalEventsWithoutUsageFailClosedAcrossNativeDialects()
    {
        var credential = new StaticNativeApiCredentialSource("secret");
        var openAi = new OpenAiResponsesStreamingProvider(
            new OpenAiResponsesProviderOptions
            {
                Model = "openai-test",
                BaseUri = new Uri("http://localhost:8046/v1"),
                AllowInsecureLoopback = true
            },
            credential,
            new FakeTransport(
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n"));
        var gemini = new GeminiInteractionsStreamingProvider(
            new GeminiInteractionsProviderOptions
            {
                Model = "gemini-test",
                BaseUri = new Uri("http://localhost:8047/v1beta"),
                AllowInsecureLoopback = true
            },
            credential,
            new FakeTransport(
                "event: interaction.completed\n" +
                "data: {\"type\":\"interaction.completed\",\"interaction\":{\"status\":\"completed\"}}\n\n"));

        var openAiError = await Assert.ThrowsAsync<ProviderException>(async () =>
            await NativeProviderTestData.ReadAsync(
                openAi,
                NativeProviderTestData.Request()));
        var geminiError = await Assert.ThrowsAsync<ProviderException>(async () =>
            await NativeProviderTestData.ReadAsync(
                gemini,
                NativeProviderTestData.Request()));

        Assert.Equal("provider_stream_protocol_error", openAiError.Code);
        Assert.Equal("provider_stream_protocol_error", geminiError.Code);
    }

    private static (string Kind, string? Text, int Input, int Output, string? Finish)
        Project(ModelStreamEvent value) =>
        (
            value.Kind,
            value.TextDelta,
            value.Usage?.InputTokens ?? 0,
            value.Usage?.OutputTokens ?? 0,
            value.FinishReason
        );
}
