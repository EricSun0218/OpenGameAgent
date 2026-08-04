using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Providers.Native;

namespace GameAgent.Providers.Native.Tests;

internal sealed class FakeTransport : INativeProviderHttpTransport
{
    private readonly int _statusCode;
    private readonly byte[] _content;

    internal FakeTransport(string content, int statusCode = 200)
    {
        _statusCode = statusCode;
        _content = Encoding.UTF8.GetBytes(content);
    }

    internal byte[]? RequestBody { get; private set; }

    internal string? CredentialHeaderName { get; private set; }

    internal Uri? RequestUri { get; private set; }

    public ValueTask<INativeProviderHttpResponse> SendAsync(
        NativeProviderHttpRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestBody = request.Body.ToArray();
        CredentialHeaderName = request.CredentialHeaderName;
        RequestUri = request.Uri;
        return new ValueTask<INativeProviderHttpResponse>(
            new FakeResponse(_statusCode, _content));
    }

    private sealed class FakeResponse : INativeProviderHttpResponse
    {
        internal FakeResponse(int statusCode, byte[] content)
        {
            StatusCode = statusCode;
            Content = new MemoryStream(content, writable: false);
        }

        public int StatusCode { get; }

        public Stream Content { get; }

        public string? GetHeader(string name) => null;

        public void Dispose() => Content.Dispose();
    }
}

internal static class NativeProviderTestData
{
    internal static StreamingModelRequest Request()
    {
        var arguments = Json("{\"x\":3}");
        var result = Json("{\"ok\":true}");
        return new StreamingModelRequest
        {
            RunId = "run-1",
            RunAttemptId = "run-attempt-1",
            TurnId = "turn-1",
            ProviderAttemptId = "provider-attempt-1",
            StreamAttemptId = "stream-1",
            MaxOutputTokens = 256,
            Messages = new[]
            {
                new NormalizedMessage
                {
                    MessageId = "m-system",
                    Role = NormalizedRoles.System,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromText("Be precise.")
                    }
                },
                new NormalizedMessage
                {
                    MessageId = "m-user",
                    Role = NormalizedRoles.User,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromJson(Json("{\"event\":\"tick\"}"))
                    }
                },
                new NormalizedMessage
                {
                    MessageId = "m-call",
                    Role = NormalizedRoles.Assistant,
                    Parts = new List<NormalizedContentPart>
                    {
                        new()
                        {
                            Type = NormalizedPartTypes.ToolCall,
                            ToolCallId = "call-old",
                            ToolName = "move",
                            Json = arguments
                        }
                    }
                },
                new NormalizedMessage
                {
                    MessageId = "m-result",
                    Role = NormalizedRoles.Tool,
                    Parts = new List<NormalizedContentPart>
                    {
                        NormalizedContentPart.FromToolResult(
                            "call-old",
                            "move",
                            result)
                    }
                }
            },
            Tools = new[]
            {
                new ToolDescriptor
                {
                    Name = "move",
                    Version = "1",
                    Description = "Move an actor.",
                    ParametersSchema = Json(
                        "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\"}},\"required\":[\"x\"],\"additionalProperties\":false}")
                }
            }
        };
    }

    internal static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    internal static async Task<List<ModelStreamEvent>> ReadAsync(
        IStreamingModelProvider provider,
        StreamingModelRequest request)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in provider.StreamAsync(request, default))
        {
            result.Add(item);
        }

        return result;
    }
}
