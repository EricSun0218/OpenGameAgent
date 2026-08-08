using System.Runtime.CompilerServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Bedrock;

internal static class AwsBedrockTransport
{
    public static async IAsyncEnumerable<BedrockProtocolEvent> StreamAsync(
        IAmazonBedrockRuntime client,
        ConverseStreamRequest request,
        IReadOnlyDictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RequestEventHandler? requestHandler = null;
        var serviceClient = client as AmazonServiceClient;
        if (serviceClient is not null && headers.Count > 0)
        {
            requestHandler = (_, args) =>
            {
                if (args is HeadersRequestEventArgs headerArgs)
                {
                    ApplyHeaders(headerArgs.Headers, headers);
                }
            };
            serviceClient.BeforeRequestEvent += requestHandler;
        }

        ConverseStreamResponse response;
        try
        {
            response = await client.ConverseStreamAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateProviderFailure(exception, null, null);
        }
        finally
        {
            if (requestHandler is not null)
            {
                serviceClient!.BeforeRequestEvent -= requestHandler;
            }
        }

        using (response)
        {
        var responseRequestId = response.ResponseMetadata?.RequestId;
        var responseStatus = (int)response.HttpStatusCode;
        var stream = response.Stream ?? throw new InvalidDataException("Bedrock returned no response stream.");
        var queue = new Queue<BedrockProtocolEvent>();
        var signal = new SemaphoreSlim(0);
        Exception? streamException = null;

        void Enqueue(BedrockProtocolEvent value)
        {
            lock (queue)
            {
                queue.Enqueue(value);
            }

            signal.Release();
        }

        stream.MessageStartReceived += (_, args) =>
            Enqueue(BedrockProtocolEvent.MessageStart(args.EventStreamEvent.Role?.Value ?? string.Empty));
        stream.ContentBlockStartReceived += (_, args) =>
        {
            var item = args.EventStreamEvent;
            Enqueue(BedrockProtocolEvent.ContentStart(
                item.ContentBlockIndex ?? 0,
                item.Start?.ToolUse?.ToolUseId,
                item.Start?.ToolUse?.Name));
        };
        stream.ContentBlockDeltaReceived += (_, args) =>
        {
            var item = args.EventStreamEvent;
            var index = item.ContentBlockIndex ?? 0;
            if (item.Delta?.Text is { } text)
            {
                Enqueue(BedrockProtocolEvent.TextDelta(index, text));
            }
            else if (item.Delta?.ToolUse?.Input is { } input)
            {
                Enqueue(BedrockProtocolEvent.ToolDelta(index, input));
            }
            else if (item.Delta?.ReasoningContent is { } reasoning)
            {
                Enqueue(BedrockProtocolEvent.ReasoningDelta(index, reasoning.Text, reasoning.Signature));
            }
        };
        stream.ContentBlockStopReceived += (_, args) =>
            Enqueue(BedrockProtocolEvent.ContentStop(args.EventStreamEvent.ContentBlockIndex ?? 0));
        stream.MessageStopReceived += (_, args) =>
            Enqueue(BedrockProtocolEvent.MessageStop(args.EventStreamEvent.StopReason?.Value ?? string.Empty));
        stream.MetadataReceived += (_, args) =>
        {
            var usage = args.EventStreamEvent.Usage;
            if (usage is not null)
            {
                Enqueue(BedrockProtocolEvent.Usage(
                    usage.InputTokens ?? 0,
                    usage.OutputTokens ?? 0,
                    usage.CacheReadInputTokens ?? 0,
                    usage.CacheWriteInputTokens ?? 0));
            }
        };
        stream.ExceptionReceived += (_, args) =>
        {
            streamException = args.EventStreamException;
            signal.Release();
        };

        Task processing;
        try
        {
            processing = stream.StartProcessingAsync();
        }
        catch (Exception exception)
        {
            throw CreateProviderFailure(exception, responseRequestId, responseStatus);
        }

        while (true)
        {
            while (TryDequeue(queue, out var item))
            {
                yield return item!;
            }

            if (processing.IsCompleted)
            {
                break;
            }

            await Task.WhenAny(signal.WaitAsync(cancellationToken), processing).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (streamException is not null)
            {
                throw CreateProviderFailure(streamException, responseRequestId, responseStatus);
            }
        }

        try
        {
            await processing.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateProviderFailure(exception, responseRequestId, responseStatus);
        }

        if (streamException is not null)
        {
            throw CreateProviderFailure(streamException, responseRequestId, responseStatus);
        }

        while (TryDequeue(queue, out var finalItem))
        {
            yield return finalItem!;
        }
        }
    }

    internal static ModelProviderException CreateProviderFailure(
        Exception exception,
        string? responseRequestId,
        int? responseStatus)
    {
        if (exception is ModelProviderException providerException)
        {
            return providerException;
        }

        var service = exception as AmazonServiceException;
        var status = service is not null && (int)service.StatusCode > 0
            ? (int)service.StatusCode
            : responseStatus is > 0 and not 200 ? responseStatus : null;
        var requestId = Bound(service?.RequestId ?? responseRequestId, 1024);
        var errorCode = Bound(service?.ErrorCode, 256);
        if (string.Equals(errorCode, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = null;
        }

        if (service is null
            && errorCode is null
            && exception.GetType() != typeof(Exception)
            && exception.GetType().Name.EndsWith("Exception", StringComparison.Ordinal)
            && exception is not IOException)
        {
            errorCode = Bound(exception.GetType().Name, 256);
        }

        var data = new Dictionary<string, object?>();
        if (status is not null)
        {
            data["status"] = status;
        }

        if (errorCode is not null)
        {
            data["errorCode"] = errorCode;
        }

        if (requestId is not null)
        {
            data["requestId"] = requestId;
        }

        var diagnostics = data.Count == 0
            ? Array.Empty<ModelDiagnostic>()
            : new[]
            {
                new ModelDiagnostic(
                    "bedrock_response_failure",
                    "The provider returned structured failure metadata.",
                    ModelDiagnosticSeverity.Error,
                    System.Text.Json.JsonSerializer.Serialize(data)),
            };
        return new ModelProviderException(exception.Message, diagnostics, exception);
    }

    private static string? Bound(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ? null : value;

    internal static void ApplyHeaders(
        IDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, string> customHeaders)
    {
        foreach (var pair in customHeaders)
        {
            requestHeaders[pair.Key] = pair.Value;
        }
    }

    private static bool TryDequeue(Queue<BedrockProtocolEvent> queue, out BedrockProtocolEvent? value)
    {
        lock (queue)
        {
            if (queue.Count > 0)
            {
                value = queue.Dequeue();
                return true;
            }
        }

        value = null;
        return false;
    }
}
